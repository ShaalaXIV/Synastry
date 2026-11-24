namespace EmoteLink;

/// <summary>The prompts that show whether or not Synastry is open (see PromptWindows.cs).</summary>
public sealed partial class Plugin
{
    private TransferOffersWindow transferOffersWindow = null!;
    private SuggestionWindow suggestionWindow = null!;
    private FreeUseWindow freeUseWindow = null!;

    partial void InitializePrompts()
    {
        windows.AddWindow(transferOffersWindow = new TransferOffersWindow(this));
        windows.AddWindow(suggestionWindow = new SuggestionWindow(this, mainWindow));
        windows.AddWindow(freeUseWindow = new FreeUseWindow(this));
        windows.AddWindow(new RoomInviteWindow(this));
        windows.AddWindow(new WizardPickWindow(this));
    }

    public int PendingOfferCount => transferOffersWindow.Count;
    public void ReviewOffers() => transferOffersWindow.Review();
    public bool IsWizardOpen => wizardWindow.IsOpen;
    /// <summary>The room's round with this animation is counting down for you.</summary>
    public bool IsRoundStarting(string modKey) =>
        modKey.Equals(pendingSelectionModKey, StringComparison.OrdinalIgnoreCase);

    private void ShowAnimationSuggestion(AnimationSuggestion suggestion) => suggestionWindow.Show(suggestion);
    private void ShowFreeUsePrompt(FreeUsePrompt prompt) => freeUseWindow.Show(prompt);

    /// <summary>What a prompt started failed after the prompt had closed. With Synastry closed nothing
    /// shows the status, so it is repeated in chat.</summary>
    private void ReportPromptFailure(string message)
    {
        Status = message;
        Chat.PrintError($"[Synastry] {message}");
    }
}
