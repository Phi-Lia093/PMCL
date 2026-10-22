using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Shared modal picker used by the version library and the per-version settings
/// page. It always exposes exactly one selected loader and removes older loader
/// profiles for the same base version after a successful installation.
/// </summary>
public sealed partial class ModLoaderSelectorViewModel : ViewModelBase
{
    private readonly ModLoaderService _modLoaderService;
    private readonly MinecraftService _minecraft;
    private readonly StoragePickerService _picker;

    private CancellationTokenSource? _loadCancellation;
    private Func<ModLoaderSelection, Task<bool>>? _installHandler;
    private IReadOnlyList<InstalledLoaderProfile> _existingLoaders = Array.Empty<InstalledLoaderProfile>();
    private GameVersion? _baseVersion;
    private bool _allowVanilla;
    private bool _availabilityError;

    public ModLoaderSelectorViewModel(
        ModLoaderService modLoaderService,
        MinecraftService minecraft,
        StoragePickerService picker)
    {
        _modLoaderService = modLoaderService;
        _minecraft = minecraft;
        _picker = picker;
    }

    public ObservableCollection<ModLoaderChoiceViewModel> Loaders { get; } = new();

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "选择模组加载器";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LoadingText { get; set; } = "正在获取可用加载器版本...";

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JavaPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetectedJavaText { get; set; } = "未检测到 Java，请手动选择。";

    [ObservableProperty]
    public partial string ExistingLoaderText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasExistingLoader { get; set; }

    [ObservableProperty]
    public partial ModLoaderChoiceViewModel? SelectedLoader { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool ShowReplacementWarning =>
        HasExistingLoader && SelectedLoader is { Kind: not ModLoaderKind.Vanilla };

    public bool SelectedLoaderNeedsJava => SelectedLoader?.Kind is
        ModLoaderKind.Forge or ModLoaderKind.NeoForge or ModLoaderKind.OptiFine;

    public string JavaRequirementText => SelectedLoaderNeedsJava
        ? "Forge / NeoForge / OptiFine 安装器需要 Java；留空时优先使用该 Minecraft 版本对应的 Java，找不到再回退到系统 Java。"
        : "Fabric 与原版安装不强制要求 Java；留空时启动游戏会自动选择 Java。";

    public bool CanInstall =>
        !IsLoading
        && !IsInstalling
        && SelectedLoader is { IsAvailable: true, SelectedVersion: not null };

    public string ConfirmText => IsInstalling ? "正在安装..." : "开始安装";

    public async Task OpenAsync(
        GameVersion baseVersion,
        string gameDirectory,
        bool allowVanilla,
        string initialJavaPath,
        Func<ModLoaderSelection, Task<bool>> installHandler)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();

        _baseVersion = baseVersion;
        _allowVanilla = allowVanilla;
        _installHandler = installHandler;
        _existingLoaders = _minecraft.GetInstalledLoaderProfiles(gameDirectory, baseVersion.Name);

        Title = $"为 Minecraft {baseVersion.Name} 选择模组加载器";
        Subtitle = allowVanilla
            ? "原版与模组加载器只能选择一个；同一游戏版本只保留一个模组加载器。"
            : "选择要添加的模组加载器；如已有加载器，安装成功后会替换旧加载器。";
        IsOpen = true;
        IsLoading = true;
        IsInstalling = false;
        ErrorMessage = string.Empty;
        _availabilityError = false;
        Loaders.Clear();
        SelectedLoader = null;

        HasExistingLoader = _existingLoaders.Count > 0;
        ExistingLoaderText = HasExistingLoader
            ? "检测到已有：" + string.Join("、", _existingLoaders.Select(loader =>
                $"{loader.DisplayName}（{loader.Name}）"))
            : string.Empty;

        var detectedJava = _minecraft.TryGetDefaultJavaPath(gameDirectory);
        JavaPath = initialJavaPath;
        DetectedJavaText = string.IsNullOrWhiteSpace(detectedJava)
            ? "未检测到 Java；安装器会在留空时尝试使用版本对应的 Java。"
            : $"自动检测：{detectedJava}";

        NotifyStateChanged();

        try
        {
            var catalog = await _modLoaderService
                .GetCatalogAsync(gameDirectory, baseVersion.Name, _loadCancellation.Token)
                .ConfigureAwait(true);

            foreach (var entry in catalog)
            {
                if (!_allowVanilla && entry.Kind == ModLoaderKind.Vanilla)
                    continue;

                var choice = new ModLoaderChoiceViewModel(entry);
                choice.PropertyChanged += OnChoicePropertyChanged;
                Loaders.Add(choice);
            }

            SelectDefaultLoader();
            if (SelectedLoader is null)
                ErrorMessage = "此 Minecraft 版本没有可用的模组加载器。";
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed while the catalog was loading.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"获取模组加载器列表失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }


    [RelayCommand]
    private async Task BrowseJavaAsync()
    {
        var file = await _picker.PickJavaExecutableAsync("选择安装模组加载器使用的 Java");
        if (!string.IsNullOrWhiteSpace(file))
        {
            JavaPath = file;
            NotifyStateChanged();
        }
    }

    [RelayCommand]
    private void ClearJava() => JavaPath = string.Empty;

    [RelayCommand]
    private void Close()
    {
        if (IsInstalling)
            return;

        _loadCancellation?.Cancel();
        IsOpen = false;
        Loaders.Clear();
        SelectedLoader = null;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (!CanInstall
            || _installHandler is null
            || _baseVersion is null
            || SelectedLoader?.SelectedVersion is null)
        {
            return;
        }

        IsInstalling = true;
        ErrorMessage = string.Empty;
        NotifyStateChanged();

        try
        {
            var selection = new ModLoaderSelection(
                SelectedLoader.Kind,
                SelectedLoader.SelectedVersion.Version,
                JavaPath.Trim(),
                _existingLoaders.Select(loader => loader.Name).ToArray());

            var succeeded = await _installHandler(selection).ConfigureAwait(true);
            if (succeeded)
            {
                IsInstalling = false;
                Close();
                return;
            }

            ErrorMessage = "安装未完成，请查看启动器底部的错误信息。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"安装失败：{ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            NotifyStateChanged();
        }
    }

    partial void OnSelectedLoaderChanged(ModLoaderChoiceViewModel? value)
    {
        if (value is not null)
        {
            if (!value.IsAvailable)
            {
                ErrorMessage = value.Catalog.Error ?? "该模组加载器当前不可用。";
                _availabilityError = true;
            }
            else if (_availabilityError)
            {
                ErrorMessage = string.Empty;
                _availabilityError = false;
            }

            var existing = _existingLoaders.FirstOrDefault(loader => loader.Kind == value.Kind);
            if (existing is not null)
                value.SelectVersion(existing.LoaderVersion);
        }

        NotifyStateChanged();
    }

    partial void OnJavaPathChanged(string value) => NotifyStateChanged();

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    private void SelectDefaultLoader()
    {
        ModLoaderChoiceViewModel? defaultChoice = null;

        if (_existingLoaders.FirstOrDefault() is { } existing)
        {
            defaultChoice = Loaders.FirstOrDefault(choice =>
                choice.Kind == existing.Kind && choice.IsAvailable);
        }

        if (defaultChoice is null && _allowVanilla)
            defaultChoice = Loaders.FirstOrDefault(choice => choice.Kind == ModLoaderKind.Vanilla);

        defaultChoice ??= Loaders.FirstOrDefault(choice =>
            choice.Kind != ModLoaderKind.Vanilla && choice.IsAvailable);

        SelectedLoader = defaultChoice ?? Loaders.FirstOrDefault(choice => choice.IsAvailable);
    }

    private void OnChoicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModLoaderChoiceViewModel.SelectedVersionIndex)
            or nameof(ModLoaderChoiceViewModel.SelectedVersion))
        {
            NotifyStateChanged();
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(ShowReplacementWarning));
        OnPropertyChanged(nameof(SelectedLoaderNeedsJava));
        OnPropertyChanged(nameof(JavaRequirementText));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(HasError));
    }
}

