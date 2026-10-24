namespace PMCL.Localization;

/// <summary>
/// Short helper used by view models, models and services.
/// </summary>
public static class L
{
    public static string Tr(string key) => LocalizationService.Instance.Get(key);

    public static string Format(string key, params object?[] args)
        => LocalizationService.Instance.Format(key, args);
}
