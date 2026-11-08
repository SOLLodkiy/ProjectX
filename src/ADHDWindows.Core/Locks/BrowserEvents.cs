namespace ADHDWindows.Core.Locks;

/// <summary>Почему браузер нельзя оставить открытым.</summary>
public enum ExtensionProblemKind
{
    /// <summary>Расширение ADHDWindows не установлено или выключено.</summary>
    Missing,

    /// <summary>Расширению не разрешено работать в окнах инкогнито, а такое окно открыто.</summary>
    Incognito,
}

/// <summary>Браузер, который сворачивается, пока не включено расширение.</summary>
public sealed record ExtensionProblem(string BrowserId, string BrowserName, ExtensionProblemKind Kind);

/// <summary>Изменился список браузеров с проблемой. Пустой список — всё в порядке.</summary>
public sealed record ExtensionProblemsChanged(IReadOnlyList<ExtensionProblem> Problems);

/// <summary>Нужно показать экран с просьбой включить расширение.</summary>
public sealed record ExtensionScreenRequested;

/// <summary>Игрок попросил открыть страницу расширений браузера.</summary>
public sealed record ExtensionPageRequested(string BrowserId);
