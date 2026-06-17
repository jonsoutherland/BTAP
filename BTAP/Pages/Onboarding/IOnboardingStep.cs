namespace BTAP.Pages.Onboarding;

/// <summary>
/// Minimal contract every onboarding step honours. Steps generally:
/// (a) write through to <see cref="Services.AppSettingsService"/> as soon as
/// the user makes a choice (so live preview "is" the commit) and
/// (b) raise <see cref="StepCompleted"/> to ask the host to advance.
///
/// Two steps need preview-and-revert behaviour (Theme, Color): they snapshot
/// the prior values in their Loaded handler and override
/// <see cref="OnRevertPreview"/> to restore on Back-without-commit.
/// </summary>
public interface IOnboardingStep
{
    /// <summary>Raised when the user has finalised their answer and the host
    /// should advance to the next step.</summary>
    event EventHandler? StepCompleted;

    /// <summary>False on terminal steps (Outro) where Back has no meaning.
    /// The host's Back button is hidden when this is false.</summary>
    bool CanGoBack { get; }

    /// <summary>Called by the host when the user navigates Back BEFORE the
    /// step's <see cref="StepCompleted"/> fired. Lets preview-style steps
    /// undo any live mutation they made to global state (theme, accent).
    /// Default: no-op.</summary>
    void RevertPreviewIfNeeded();
}
