using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
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
    private readonly JavaRuntimeService _javaRuntimes;

    private CancellationTokenSource? _loadCancellation;
    private Func<ModLoaderSelection, Task<bool>>? _installHandler;
    private IReadOnlyList<InstalledLoaderProfile> _existingLoaders = Array.Empty<InstalledLoaderProfile>();
    private GameVersion? _baseVersion;
    private string _gameDirectory = string.Empty;
    private bool _allowVanilla;
    private bool _availabilityError;
    private bool _syncingJavaSelection;

    public ModLoaderSelectorViewModel(
        ModLoaderService modLoaderService,
        MinecraftService minecraft,
        StoragePickerService picker,
        JavaRuntimeService javaRuntimes)
    {
        _modLoaderService = modLoaderService;
        _minecraft = minecraft;
        _picker = picker;
        _javaRuntimes = javaRuntimes;
    }

    public ObservableCollection<ModLoaderChoiceViewModel> Loaders { get; } = new();

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = L.Tr("Loader.Title");

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LoadingText { get; set; } = L.Tr("Loader.Loading");

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JavaPath { get; set; } = string.Empty;

    public ObservableCollection<JavaRuntimeOption> JavaRuntimeOptions { get; } = new();

    [ObservableProperty]
    public partial JavaRuntimeOption? SelectedJavaRuntime { get; set; }

    [ObservableProperty]
    public partial bool IsScanningJava { get; set; }

    [ObservableProperty]
    public partial string JavaScanStatus { get; set; } = L.Tr("Java.NotScanned");

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
        ? L.Tr("Loader.JavaHintInstaller")
        : L.Tr("Loader.JavaHintFabric");

    public bool CanInstall =>
        !IsLoading
        && !IsInstalling
        && SelectedLoader is { IsAvailable: true, SelectedVersion: not null };

    public string ConfirmText => IsInstalling
        ? L.Tr("Loader.Installing")
        : L.Tr("Loader.StartInstall");

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
        _gameDirectory = gameDirectory;
        _allowVanilla = allowVanilla;
        _installHandler = installHandler;
        _existingLoaders = _minecraft.GetInstalledLoaderProfiles(gameDirectory, baseVersion.Name);

        Title = L.Format("Loader.TitleForVersion", baseVersion.Name);
        Subtitle = allowVanilla
            ? L.Tr("Loader.ModeVanillaHint")
            : L.Tr("Loader.ModeAddHint");
        IsOpen = true;
        IsLoading = true;
        IsInstalling = false;
        ErrorMessage = string.Empty;
        _availabilityError = false;
        Loaders.Clear();
        SelectedLoader = null;

        HasExistingLoader = _existingLoaders.Count > 0;
        ExistingLoaderText = HasExistingLoader
            ? L.Format(
                "Loader.Existing",
                string.Join(
                    L.Tr("Common.ListSeparator"),
                    _existingLoaders.Select(loader => L.Format(
                        "Loader.ExistingItem",
                        loader.DisplayName,
                        loader.Name))))
            : string.Empty;

        JavaPath = initialJavaPath;
        RefreshJavaRuntimeOptions(JavaPath);

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
                ErrorMessage = L.Tr("Loader.NoLoaders");
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed while the catalog was loading.
        }
        catch (Exception ex)
        {
            ErrorMessage = L.Format("Loader.LoadFailed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }


    [RelayCommand]
    private async Task ScanJavaAsync()
    {
        if (IsScanningJava || _baseVersion is null)
            return;

        IsScanningJava = true;
        JavaScanStatus = L.Tr("Java.Scanning");

        try
        {
            var result = await _javaRuntimes
                .ScanAsync(_gameDirectory)
                .ConfigureAwait(true);

            RefreshJavaRuntimeOptions(JavaPath);
            JavaScanStatus = L.Format("Java.ScanComplete", result.Runtimes.Count);
        }
        catch (Exception ex)
        {
            JavaScanStatus = L.Format("Java.ScanFailed", ex.Message);
        }
        finally
        {
            IsScanningJava = false;
        }
    }

    [RelayCommand]
    private async Task BrowseJavaAsync()
    {
        var file = await _picker.PickJavaExecutableAsync(L.Tr("Loader.PickJava"));
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

            ErrorMessage = L.Tr("Loader.InstallIncomplete");
        }
        catch (Exception ex)
        {
            ErrorMessage = L.Format("Loader.InstallFailed", ex.Message);
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
                ErrorMessage = value.Catalog.Error ?? L.Tr("Loader.UnavailableForVersion");
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

    partial void OnJavaPathChanged(string value)
    {
        NotifyStateChanged();

        if (!_syncingJavaSelection)
            RefreshJavaRuntimeOptions(value);
    }

    partial void OnSelectedJavaRuntimeChanged(JavaRuntimeOption? value)
    {
        if (_syncingJavaSelection || value is null)
            return;

        var path = value.IsAuto ? string.Empty : value.Path;
        if (string.Equals(JavaPath, path, StringComparison.Ordinal))
            return;

        _syncingJavaSelection = true;
        try
        {
            JavaPath = path;
        }
        finally
        {
            _syncingJavaSelection = false;
        }
    }

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

    private void RefreshJavaRuntimeOptions(string? path)
    {
        var desiredPath = path?.Trim() ?? string.Empty;
        var options = new List<JavaRuntimeOption>
        {
            new(string.Empty, L.Tr("Java.AutoDownload"))
        };

        foreach (var runtime in _javaRuntimes.CachedRuntimes)
        {
            if (options.Any(option => string.Equals(
                    option.Path,
                    runtime.Path,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            options.Add(new JavaRuntimeOption(runtime.Path, runtime.DisplayName));
        }

        if (desiredPath.Length > 0
            && options.All(option => !string.Equals(
                option.Path,
                desiredPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new JavaRuntimeOption(
                desiredPath,
                L.Format("Java.CustomRuntime", desiredPath)));
        }

        _syncingJavaSelection = true;
        try
        {
            JavaRuntimeOptions.Clear();
            foreach (var option in options)
                JavaRuntimeOptions.Add(option);

            SelectedJavaRuntime = JavaRuntimeOptions.FirstOrDefault(option =>
                                    string.Equals(
                                        option.Path,
                                        desiredPath,
                                        StringComparison.OrdinalIgnoreCase))
                                ?? JavaRuntimeOptions[0];
        }
        finally
        {
            _syncingJavaSelection = false;
        }

        var lastScannedAt = _javaRuntimes.LastScannedAt;
        JavaScanStatus = lastScannedAt is null
            ? L.Tr("Java.NotScanned")
            : L.Format(
                "Java.LastScan",
                lastScannedAt.Value.LocalDateTime.ToString("g"),
                _javaRuntimes.CachedRuntimes.Count);
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

