using System.Collections.ObjectModel;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.ViewModels;

internal sealed class MainWindowViewModel : ObservableObject
{
    private readonly DemoInstallerFrontendSession session;
    private FolderChoice selectedFolder;
    private ReleaseChoice selectedRelease;
    private OperationChoice selectedOperation;
    private string previewHeading = "Choose settings and preview an operation";
    private string previewSummary = "The preview is synthetic. The production installer backend is not connected in this build.";
    private string previewAnnouncement = "No synthetic preview yet. Backend disconnected and durable state unchanged.";
    private string stateLabel = "Unchanged — no session started";

    public MainWindowViewModel(DemoInstallerFrontendSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        if (session.Folders.Count == 0 || session.Releases.Count == 0 || session.Operations.Count == 0)
            throw new ArgumentException("The frontend session must provide selectable demo options.", nameof(session));

        this.selectedFolder = session.Folders[0];
        this.selectedRelease = session.Releases[0];
        this.selectedOperation = session.Operations[0];
        this.PreviewCommand = new RelayCommand(this.CreatePreview);
        this.ResetCommand = new RelayCommand(this.Reset);
        this.ExecuteCommand = new RelayCommand(() => { }, () => false);

        this.LogEntries.Add("DEMO  Ready. Synthetic selections are loaded; the installer backend is disconnected.");
        this.LogEntries.Add("SAFE  No game folder, Mods folder, save, package, or network resource has been accessed.");
    }

    public string SafetyBanner => "SAFE DEMO MODE — synthetic data only. Backend disconnected. No game, Mods, saves, packages, or network are accessed.";

    public IReadOnlyList<FolderChoice> Folders => this.session.Folders;

    public IReadOnlyList<ReleaseChoice> Releases => this.session.Releases;

    public IReadOnlyList<OperationChoice> Operations => this.session.Operations;

    public ObservableCollection<string> LogEntries { get; } = [];

    public RelayCommand PreviewCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand ExecuteCommand { get; }

    public bool IsBackendConnected => false;

    public FolderChoice SelectedFolder
    {
        get => this.selectedFolder;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (this.SetProperty(ref this.selectedFolder, value))
                this.ClearStalePreview();
        }
    }

    public ReleaseChoice SelectedRelease
    {
        get => this.selectedRelease;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (this.SetProperty(ref this.selectedRelease, value))
                this.ClearStalePreview();
        }
    }

    public OperationChoice SelectedOperation
    {
        get => this.selectedOperation;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (this.SetProperty(ref this.selectedOperation, value))
                this.ClearStalePreview();
        }
    }

    public string FolderDetail => this.SelectedFolder.Detail;

    public string ReleaseDetail => this.SelectedRelease.Detail;

    public string OperationDetail => this.SelectedOperation.Summary;

    public string PreviewHeading
    {
        get => this.previewHeading;
        private set => this.SetProperty(ref this.previewHeading, value);
    }

    public string PreviewSummary
    {
        get => this.previewSummary;
        private set => this.SetProperty(ref this.previewSummary, value);
    }

    public string StateLabel
    {
        get => this.stateLabel;
        private set => this.SetProperty(ref this.stateLabel, value);
    }

    public string PreviewAnnouncement
    {
        get => this.previewAnnouncement;
        private set => this.SetProperty(ref this.previewAnnouncement, value);
    }

    private void CreatePreview()
    {
        FrontendPreview preview = this.session.CreatePreview(this.SelectedFolder, this.SelectedRelease, this.SelectedOperation);
        this.PreviewHeading = preview.Heading;
        this.PreviewSummary = preview.Summary;
        this.StateLabel = preview.StateLabel;
        this.PreviewAnnouncement = $"{preview.Heading}. Durable state unchanged. No backend action ran.";
        DemoText.Validate(this.PreviewAnnouncement);
        this.LogEntries.Clear();
        foreach (string entry in preview.LogEntries)
            this.LogEntries.Add(entry);
    }

    private void Reset()
    {
        this.SelectedFolder = this.Folders[0];
        this.SelectedRelease = this.Releases[0];
        this.SelectedOperation = this.Operations[0];
        this.LogEntries.Clear();
        this.LogEntries.Add("DEMO  Selections reset. No backend action ran and no files changed.");
        this.ClearStalePreview();
    }

    private void ClearStalePreview()
    {
        this.PreviewHeading = "Settings changed — preview again";
        this.PreviewSummary = "No backend action has run. Generate another synthetic preview to review this selection.";
        this.StateLabel = "Unchanged — backend disconnected";
        this.PreviewAnnouncement = "Settings changed. Preview again. No backend action ran.";
        this.OnPropertyChanged(nameof(this.FolderDetail));
        this.OnPropertyChanged(nameof(this.ReleaseDetail));
        this.OnPropertyChanged(nameof(this.OperationDetail));
    }
}
