using System;
using System.Collections.Immutable;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI.Framework.Health.Viewer.Content;
using StardewModdingAPI.Framework.Health.Viewer.Layout;
using StardewValley;
using StardewValley.Menus;

namespace StardewModdingAPI.Framework.Health.Viewer.Game;

/// <summary>A thin game-thread renderer/input adapter over one exact prepared health-report session.</summary>
internal sealed class ModHealthReportMenu : IClickableMenu
{
    private static readonly Color PanelColor = new(24, 24, 30, 242);
    private static readonly Color HeaderColor = new(43, 48, 60, 255);
    private static readonly Color SelectedColor = new(77, 105, 150, 255);
    private static readonly Color HoverColor = new(58, 65, 79, 255);
    private static readonly Color PrivacyColor = new(70, 56, 35, 255);

    private readonly ModHealthViewerSession Session;
    private readonly Func<string, string> Translate;
    private readonly ModHealthViewerLayout Layout = new();
    private readonly ModHealthViewerNavigationState Navigation = new();
    private readonly ModHealthViewerNavigationState DetailNavigation = new();

    private ImmutableArray<ModHealthViewerDisplayRow> VisibleRows = ImmutableArray<ModHealthViewerDisplayRow>.Empty;
    private ImmutableArray<ModHealthViewerDetailRow> VisibleDetails = ImmutableArray<ModHealthViewerDetailRow>.Empty;
    private ModHealthViewerFocusTarget Focus = new(ModHealthViewerTargetKind.Section, 0);
    private ModHealthViewerFocusTarget Hover = ModHealthViewerFocusTarget.None;
    private int LastViewportWidth;
    private int LastViewportHeight;
    private float LastUiScale;
    private int LastSection = -1;
    private int LastFirstVisible = -1;
    private int LastRowCount = -1;
    private int LastActionCount = -1;
    private long LastProjectionRevision = -1;
    private int SelectedSummaryRow = -1;
    private bool ShowingDetails;
    private ModHealthViewerExpandedText? ExpandedText;
    private Guid DisplayedRequestId;
    private long DisplayedProjectionRevision;
    private ModHealthViewerContentAdapter? DisplayedContent;
    private bool HasReleasedOwnership;

    public ModHealthReportMenu(ModHealthViewerSession session, Func<string, string> translate)
    {
        this.Session = session ?? throw new ArgumentNullException(nameof(session));
        this.Translate = translate ?? throw new ArgumentNullException(nameof(translate));
        this.DisplayedRequestId = session.RequestId;
        this.DisplayedProjectionRevision = session.ProjectionRevision;
        this.DisplayedContent = session.Content;
        this.Recompute(force: true);
    }

    public Guid ViewerInstanceId => this.Session.ViewerInstanceId;

    public override void update(GameTime time)
    {
        base.update(time);
        // Read-only exact-request polling; coordinator mutations remain queued for SCore's safe drain.
        this.GetController()?.UpdateOwnedViewer(this.ViewerInstanceId);
        this.Recompute(force: false);
    }

    protected override void cleanupBeforeExit()
    {
        this.NotifyOwnershipReleased();
        base.cleanupBeforeExit();
    }

    public override void emergencyShutDown()
    {
        this.NotifyOwnershipReleased();
        base.emergencyShutDown();
    }

    /// <summary>Bind the controller after safe attachment, avoiding any global lookup or public API.</summary>
    internal ModHealthViewerController? Controller { get; set; }

    private ModHealthViewerController? GetController() => this.Controller;

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        this.Recompute(force: true);
    }

    public override void performHoverAction(int x, int y)
    {
        this.Hover = this.Layout.HitTest(x, y);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.Layout.PrivacyNoticeBounds.Contains(x, y))
        {
            this.OpenPrivacyExpanded();
            return;
        }
        if (this.Layout.ContentHeaderBounds.Contains(x, y))
        {
            this.OpenStatusExpanded();
            return;
        }
        this.Activate(this.Layout.HitTest(x, y));
    }

    public override void receiveScrollWheelAction(int direction)
    {
        this.ApplyInput(ModHealthViewerInputRouter.MapWheel(direction));
    }

    public override void receiveKeyPress(Keys key)
    {
        if (ModHealthViewerInputRouter.TryMapKey(key, out ModHealthViewerInput input))
            this.ApplyInput(input);
    }

    public override void receiveGamePadButton(Buttons button)
    {
        if (ModHealthViewerInputRouter.TryMapButton(button, out ModHealthViewerInput input))
            this.ApplyInput(input);
    }

    public override void draw(SpriteBatch b)
    {
        ModHealthLayoutRectangle menu = this.Layout.MenuBounds;
        Fill(b, menu, PanelColor);
        Fill(b, this.Layout.HeaderBounds, HeaderColor);
        Fill(b, this.Layout.PrivacyNoticeBounds, PrivacyColor);

        ModHealthLayoutRectangle titleBounds = new(
            this.Layout.HeaderBounds.X,
            this.Layout.HeaderBounds.Y,
            Math.Max(0, this.Layout.CloseBounds.X - this.Layout.HeaderBounds.X - 4),
            this.Layout.HeaderBounds.Height
        );
        DrawText(b, this.Translate(ModHealthViewerTranslationKeys.Title), titleBounds, Color.White, 8, 4);
        DrawCenteredText(b, this.Translate(ModHealthViewerTranslationKeys.CloseGlyph), this.Layout.CloseBounds, Color.White);
        DrawText(b, this.Translate(ModHealthViewerTranslationKeys.Privacy), this.Layout.PrivacyNoticeBounds, Color.Wheat, 8, 4);

        for (int i = 0; i < ModHealthViewerLayout.SectionCount; i++)
        {
            ModHealthLayoutRectangle bounds = this.Layout.GetSectionBounds(i);
            ModHealthViewerFocusTarget target = new(ModHealthViewerTargetKind.Section, i);
            Fill(b, bounds, this.Navigation.SectionIndex == i ? SelectedColor : this.Hover == target || this.Focus == target ? HoverColor : HeaderColor);
            DrawText(b, this.Translate(ModHealthViewerTranslationKeys.Section((ModHealthViewerSection)i)), bounds, Color.White, 6, 4);
        }

        string stateText = this.Translate(ModHealthViewerTranslationKeys.State(this.Session.PreparedState));
        if (this.ShowingDetails || this.ExpandedText is not null)
            stateText += $" · {this.Translate(ModHealthViewerTranslationKeys.Details)}";
        if (this.ExpandedText is not null)
            stateText += $" · {this.ExpandedText.PageIndex + 1}/{this.ExpandedText.PageCount}";
        DrawText(b, stateText, this.Layout.ContentHeaderBounds, StateColor(this.Session.PreparedState), 8, 4);
        bool actionRejected = this.Session.LastActionDisposition == ModHealthViewerActionDisposition.RejectedFull;
        DrawText(
            b,
            actionRejected ? this.Translate(ModHealthViewerTranslationKeys.OperationRefused) : this.GetExactRequestLine(),
            this.Layout.ContentHeaderBounds,
            actionRejected ? Color.OrangeRed : Color.LightGray,
            8,
            Math.Max(0, this.Layout.ContentHeaderBounds.Height - Game1.smallFont.LineSpacing)
        );
        if (this.Session.PreparedState == ModHealthPreparedReportState.WriteFailed)
            DrawText(b, this.Translate(ModHealthViewerTranslationKeys.NotSaved), this.Layout.PrivacyNoticeBounds, Color.OrangeRed, 8, 28);

        if (this.ExpandedText is not null)
        {
            int lineHeight = Math.Max(1, Game1.smallFont.LineSpacing);
            for (int i = 0; i < this.ExpandedText.CurrentLineCount; i++)
            {
                ModHealthLayoutRectangle bounds = new(
                    this.Layout.RowViewportBounds.X,
                    this.Layout.RowViewportBounds.Y + i * lineHeight,
                    this.Layout.RowViewportBounds.Width,
                    lineHeight
                );
                DrawText(b, this.ExpandedText.GetCurrentLine(i), bounds, i == 0 ? Color.LightBlue : Color.White, 4, 0);
            }
        }
        else if (this.ShowingDetails)
        {
            for (int i = 0; i < this.VisibleDetails.Length; i++)
            {
                ModHealthLayoutRectangle bounds = this.Layout.GetVisibleRowBounds(i);
                int absoluteIndex = this.Layout.FirstVisibleRow + i;
                ModHealthViewerFocusTarget target = new(ModHealthViewerTargetKind.Row, absoluteIndex);
                Fill(b, bounds, this.DetailNavigation.RowIndex == absoluteIndex ? SelectedColor : this.Hover == target || this.Focus == target ? HoverColor : PanelColor);
                ModHealthViewerDetailRow row = this.VisibleDetails[i];
                DrawText(b, row.Label, bounds, Color.LightBlue, 8, 3);
                DrawText(b, row.Value, bounds, Color.LightGray, 8, 25);
            }
        }
        else
        {
            for (int i = 0; i < this.VisibleRows.Length; i++)
            {
                ModHealthLayoutRectangle bounds = this.Layout.GetVisibleRowBounds(i);
                int absoluteIndex = this.Layout.FirstVisibleRow + i;
                ModHealthViewerFocusTarget target = new(ModHealthViewerTargetKind.Row, absoluteIndex);
                Fill(b, bounds, this.Navigation.RowIndex == absoluteIndex ? SelectedColor : this.Hover == target || this.Focus == target ? HoverColor : PanelColor);
                ModHealthViewerDisplayRow row = this.VisibleRows[i];
                DrawText(b, $"{ModHealthViewerText.GetRowCue(row.Severity, row.IconKey)} {row.Title}", bounds, RowColor(row.Severity), 8, 3);
                DrawText(b, row.Detail, bounds, Color.LightGray, 8, 25);
            }
        }

        for (int i = 0; i < this.Session.AvailableActionCount; i++)
        {
            ModHealthLayoutRectangle bounds = this.Layout.GetActionBounds(i);
            ModHealthViewerFocusTarget target = new(ModHealthViewerTargetKind.Action, i);
            Fill(b, bounds, this.Hover == target || this.Focus == target ? SelectedColor : HeaderColor);
            DrawText(b, this.Translate(ModHealthViewerTranslationKeys.Action(this.Session.GetAvailableAction(i))), bounds, Color.White, 6, 8);
        }

        if (this.Layout.ScrollThumbBounds.Height > 0)
        {
            Fill(b, this.Layout.ScrollTrackBounds, HeaderColor);
            Fill(b, this.Layout.ScrollThumbBounds, SelectedColor);
        }
        this.drawMouse(b);
        if (actionRejected)
            this.Session.AcknowledgeActionFeedbackRendered();
    }

    private void Activate(ModHealthViewerFocusTarget target)
    {
        this.Focus = target;
        switch (target.Kind)
        {
            case ModHealthViewerTargetKind.Close:
                this.RequestViewerClose();
                break;
            case ModHealthViewerTargetKind.Section:
                this.SelectSection(target.Index);
                break;
            case ModHealthViewerTargetKind.Row:
                if (!ModHealthViewerInputRouter.CanActivateRow(target.Index, this.GetCurrentRowCount()))
                {
                    this.Focus = new(ModHealthViewerTargetKind.Section, this.Navigation.SectionIndex);
                    break;
                }
                if (this.ShowingDetails)
                {
                    this.DetailNavigation.SelectVisibleRow(target.Index - this.DetailNavigation.FirstVisibleRow, this.GetCurrentRowCount());
                    this.OpenSelectedExpanded();
                }
                else
                {
                    this.Navigation.SelectVisibleRow(target.Index - this.Navigation.FirstVisibleRow, this.GetCurrentRowCount());
                    this.OpenSelectedDetails();
                }
                break;
            case ModHealthViewerTargetKind.Action when (uint)target.Index < this.Session.AvailableActionCount:
                ModHealthViewerActionKind action = this.Session.GetAvailableAction(target.Index);
                if (action == ModHealthViewerActionKind.Close)
                    this.RequestViewerClose();
                else
                    this.Session.QueueAction(action);
                break;
        }
    }

    private void ApplyInput(ModHealthViewerInput input)
    {
        if (this.ExpandedText is not null)
        {
            if (input.Kind == ModHealthViewerInputKind.Navigate)
            {
                this.ExpandedText.Apply(input.Navigation);
                this.Recompute(force: true);
            }
            else if (input.Kind == ModHealthViewerInputKind.MoveFocus)
            {
                this.ExpandedText.Apply(input.FocusDirection is ModHealthViewerFocusDirection.Up or ModHealthViewerFocusDirection.Left
                    ? ModHealthViewerNavigationCommand.PreviousRow
                    : ModHealthViewerNavigationCommand.NextRow);
                this.Recompute(force: true);
            }
            else if (input.Kind is ModHealthViewerInputKind.Close or ModHealthViewerInputKind.Activate)
                this.CloseExpanded();
            else if (input.Kind == ModHealthViewerInputKind.ExpandStatus)
                this.OpenStatusExpanded();
            else if (input.Kind == ModHealthViewerInputKind.ExpandPrivacy)
                this.OpenPrivacyExpanded();
            return;
        }

        switch (input.Kind)
        {
            case ModHealthViewerInputKind.Navigate:
                this.Navigate(input.Navigation);
                break;
            case ModHealthViewerInputKind.MoveFocus:
                this.MoveFocus(input.FocusDirection);
                break;
            case ModHealthViewerInputKind.CycleFocus:
                this.Layout.TryCycleFocus(this.Focus, backwards: false, out this.Focus);
                break;
            case ModHealthViewerInputKind.Activate:
                if (this.ShowingDetails && this.Focus.Kind == ModHealthViewerTargetKind.Row)
                    this.OpenSelectedExpanded();
                else
                    this.Activate(this.Focus);
                break;
            case ModHealthViewerInputKind.ExpandStatus:
                this.OpenStatusExpanded();
                break;
            case ModHealthViewerInputKind.ExpandPrivacy:
                this.OpenPrivacyExpanded();
                break;
            case ModHealthViewerInputKind.Close:
                this.RequestClose();
                break;
        }
    }

    private void Navigate(ModHealthViewerNavigationCommand command)
    {
        if (this.ShowingDetails && command is ModHealthViewerNavigationCommand.PreviousSection or ModHealthViewerNavigationCommand.NextSection)
            this.CloseDetails();
        ModHealthViewerNavigationState activeNavigation = this.ShowingDetails ? this.DetailNavigation : this.Navigation;
        int oldSection = this.Navigation.SectionIndex;
        int rowCount = this.GetCurrentRowCount();
        activeNavigation.Apply(command, ModHealthViewerLayout.SectionCount, rowCount);
        if (this.Navigation.SectionIndex != oldSection)
            this.Navigation.SelectSection(this.Navigation.SectionIndex, ModHealthViewerLayout.SectionCount, this.GetCurrentRowCount());
        rowCount = this.GetCurrentRowCount();
        this.Focus = ModHealthViewerInputRouter.GetRowFocus(this.Navigation.SectionIndex, activeNavigation.RowIndex, rowCount);
        this.Recompute(force: true);
    }

    private void MoveFocus(ModHealthViewerFocusDirection direction)
    {
        if (this.Focus.Kind == ModHealthViewerTargetKind.Row)
        {
            ModHealthViewerNavigationState activeNavigation = this.ShowingDetails ? this.DetailNavigation : this.Navigation;
            int rowCount = this.GetCurrentRowCount();
            if (direction == ModHealthViewerFocusDirection.Up && activeNavigation.RowIndex > 0)
            {
                this.Navigate(ModHealthViewerNavigationCommand.PreviousRow);
                return;
            }
            if (direction == ModHealthViewerFocusDirection.Down && activeNavigation.RowIndex < rowCount - 1)
            {
                this.Navigate(ModHealthViewerNavigationCommand.NextRow);
                return;
            }
        }
        if (this.Layout.TryMoveFocus(this.Focus, direction, out ModHealthViewerFocusTarget next))
            this.Focus = next;
    }

    private void SelectSection(int sectionIndex)
    {
        this.ExpandedText = null;
        this.ShowingDetails = false;
        this.SelectedSummaryRow = -1;
        this.Navigation.SelectSection(sectionIndex, ModHealthViewerLayout.SectionCount, this.GetRowCount((ModHealthViewerSection)sectionIndex));
        this.Focus = new(ModHealthViewerTargetKind.Section, this.Navigation.SectionIndex);
        this.Recompute(force: true);
    }

    private void Recompute(bool force)
    {
        this.SynchronizeModeWithSession();
        int viewportWidth = Math.Max(1, Game1.uiViewport.Width);
        int viewportHeight = Math.Max(1, Game1.uiViewport.Height);
        float uiScale = Game1.options?.uiScale ?? 1;
        int rowCount = this.ExpandedText is null ? this.GetCurrentRowCount() : 0;
        if (!force
            && viewportWidth == this.LastViewportWidth
            && viewportHeight == this.LastViewportHeight
            && Math.Abs(uiScale - this.LastUiScale) < 0.0001f
            && this.Navigation.SectionIndex == this.LastSection
            && (this.ShowingDetails ? this.DetailNavigation.FirstVisibleRow : this.Navigation.FirstVisibleRow) == this.LastFirstVisible
            && rowCount == this.LastRowCount
            && this.Session.AvailableActionCount == this.LastActionCount
            && this.Session.ProjectionRevision == this.LastProjectionRevision)
        {
            return;
        }

        ModHealthViewerNavigationState activeNavigation = this.ShowingDetails ? this.DetailNavigation : this.Navigation;
        int preferredNavigationWidth = this.MeasurePreferredNavigationWidth();
        int preferredActionWidth = this.MeasurePreferredActionWidth();
        this.Layout.Recompute(new(viewportWidth, viewportHeight, uiScale, rowCount, activeNavigation.FirstVisibleRow, this.Session.AvailableActionCount, preferredNavigationWidth, preferredActionWidth));
        if (this.ExpandedText is null)
        {
            activeNavigation.SetVisibleRowCount(this.Layout.VisibleRowCapacity, rowCount);
            if (this.Layout.FirstVisibleRow != activeNavigation.FirstVisibleRow)
                this.Layout.Recompute(new(viewportWidth, viewportHeight, uiScale, rowCount, activeNavigation.FirstVisibleRow, this.Session.AvailableActionCount, preferredNavigationWidth, preferredActionWidth));
        }
        if (this.ExpandedText is not null)
        {
            int lineHeight = Math.Max(1, Game1.smallFont.LineSpacing);
            int linesPerPage = Math.Max(1, this.Layout.RowViewportBounds.Height / lineHeight);
            this.ExpandedText.Reflow(Math.Max(1, this.Layout.RowViewportBounds.Width - 8), linesPerPage, MeasureSmallText);
            this.VisibleRows = ImmutableArray<ModHealthViewerDisplayRow>.Empty;
            this.VisibleDetails = ImmutableArray<ModHealthViewerDetailRow>.Empty;
        }
        else if (this.ShowingDetails && this.Session.Content is ModHealthViewerContentAdapter content)
        {
            this.VisibleDetails = content.GetDetailPage((ModHealthViewerSection)this.Navigation.SectionIndex, this.SelectedSummaryRow, this.Layout.FirstVisibleRow, this.Layout.VisibleRowCount);
            this.VisibleRows = ImmutableArray<ModHealthViewerDisplayRow>.Empty;
        }
        else
        {
            this.VisibleRows = this.Session.Content?.GetPage((ModHealthViewerSection)this.Navigation.SectionIndex, this.Layout.FirstVisibleRow, this.Layout.VisibleRowCount)
                ?? ImmutableArray<ModHealthViewerDisplayRow>.Empty;
            this.VisibleDetails = ImmutableArray<ModHealthViewerDetailRow>.Empty;
        }

        this.xPositionOnScreen = this.Layout.MenuBounds.X;
        this.yPositionOnScreen = this.Layout.MenuBounds.Y;
        this.width = this.Layout.MenuBounds.Width;
        this.height = this.Layout.MenuBounds.Height;
        this.LastViewportWidth = viewportWidth;
        this.LastViewportHeight = viewportHeight;
        this.LastUiScale = uiScale;
        this.LastSection = this.Navigation.SectionIndex;
        this.LastFirstVisible = activeNavigation.FirstVisibleRow;
        this.LastRowCount = rowCount;
        this.LastActionCount = this.Session.AvailableActionCount;
        this.LastProjectionRevision = this.Session.ProjectionRevision;
    }

    private int GetCurrentRowCount()
    {
        if (!this.ShowingDetails || this.Session.Content is not ModHealthViewerContentAdapter content || this.SelectedSummaryRow < 0)
            return this.GetRowCount((ModHealthViewerSection)this.Navigation.SectionIndex);
        int summaryCount = content.GetRowCount((ModHealthViewerSection)this.Navigation.SectionIndex);
        if (this.SelectedSummaryRow >= summaryCount)
        {
            this.ShowingDetails = false;
            this.SelectedSummaryRow = -1;
            return summaryCount;
        }
        return content.GetDetailRowCount((ModHealthViewerSection)this.Navigation.SectionIndex, this.SelectedSummaryRow);
    }

    private int GetRowCount(ModHealthViewerSection section) => this.Session.Content?.GetRowCount(section) ?? 0;

    private void OpenSelectedDetails()
    {
        if (this.Session.Content is not ModHealthViewerContentAdapter content)
            return;
        this.SelectedSummaryRow = this.Navigation.RowIndex;
        if (!ModHealthViewerInputRouter.CanActivateRow(this.SelectedSummaryRow, content.GetRowCount((ModHealthViewerSection)this.Navigation.SectionIndex)))
        {
            this.SelectedSummaryRow = -1;
            this.Focus = new(ModHealthViewerTargetKind.Section, this.Navigation.SectionIndex);
            return;
        }
        int count = content.GetDetailRowCount((ModHealthViewerSection)this.Navigation.SectionIndex, this.SelectedSummaryRow);
        this.ShowingDetails = true;
        this.DetailNavigation.SelectSection(this.Navigation.SectionIndex, ModHealthViewerLayout.SectionCount, count);
        this.Focus = new(ModHealthViewerTargetKind.Row, 0);
        this.Recompute(force: true);
    }

    private void CloseDetails()
    {
        this.ExpandedText = null;
        this.ShowingDetails = false;
        this.SelectedSummaryRow = -1;
        this.Focus = new(ModHealthViewerTargetKind.Row, this.Navigation.RowIndex);
        this.Recompute(force: true);
    }

    private void OpenSelectedExpanded()
    {
        if (this.Session.Content is not ModHealthViewerContentAdapter content || !this.ShowingDetails)
            return;
        ImmutableArray<ModHealthViewerDetailRow> selected = content.GetDetailPage(
            (ModHealthViewerSection)this.Navigation.SectionIndex,
            this.SelectedSummaryRow,
            this.DetailNavigation.RowIndex,
            1
        );
        if (selected.Length == 0)
            return;
        this.ExpandedText = new(selected[0].Label, selected[0].Value);
        this.Recompute(force: true);
    }

    private void OpenStatusExpanded()
    {
        this.ExpandedText = new(
            this.Translate(ModHealthViewerTranslationKeys.State(this.Session.PreparedState)),
            this.GetExactRequestLine()
        );
        this.Recompute(force: true);
    }

    private void OpenPrivacyExpanded()
    {
        this.ExpandedText = new(
            this.Translate(ModHealthViewerTranslationKeys.Privacy),
            this.Session.PreparedState == ModHealthPreparedReportState.WriteFailed
                ? this.Translate(ModHealthViewerTranslationKeys.NotSaved)
                : string.Empty
        );
        this.Recompute(force: true);
    }

    private void CloseExpanded()
    {
        this.ExpandedText = null;
        this.Focus = this.ShowingDetails
            ? new(ModHealthViewerTargetKind.Row, this.DetailNavigation.RowIndex)
            : new(ModHealthViewerTargetKind.Row, this.Navigation.RowIndex);
        this.Recompute(force: true);
    }

    private void SynchronizeModeWithSession()
    {
        bool contentModeInvalid = ModHealthViewerInputRouter.ShouldLeaveContentMode(
            this.DisplayedRequestId,
            this.Session.RequestId,
            this.DisplayedProjectionRevision,
            this.Session.ProjectionRevision,
            this.DisplayedContent,
            this.Session.Content
        );
        if (contentModeInvalid && (this.ShowingDetails || this.ExpandedText is not null))
        {
            this.ExpandedText = null;
            this.ShowingDetails = false;
            this.SelectedSummaryRow = -1;
            this.Focus = new(ModHealthViewerTargetKind.Section, this.Navigation.SectionIndex);
        }
        this.DisplayedRequestId = this.Session.RequestId;
        this.DisplayedProjectionRevision = this.Session.ProjectionRevision;
        this.DisplayedContent = this.Session.Content;
    }

    private void RequestClose()
    {
        if (this.ExpandedText is not null)
        {
            this.CloseExpanded();
            return;
        }
        if (ModHealthViewerInputRouter.ResolveClose(this.ShowingDetails) == ModHealthViewerCloseBehavior.CloseDetails)
            this.CloseDetails();
        else
            this.Session.QueueAction(ModHealthViewerActionKind.Close);
    }

    private void RequestViewerClose()
    {
        this.Session.QueueAction(ModHealthViewerActionKind.Close);
    }

    private void NotifyOwnershipReleased()
    {
        if (this.HasReleasedOwnership)
            return;
        this.HasReleasedOwnership = true;
        this.Controller?.HandleViewerClosed(this.ViewerInstanceId);
    }

    private string GetExactRequestLine()
    {
        if (this.Session.NewerRequestId is Guid newerId)
            return $"{this.Translate(ModHealthViewerTranslationKeys.NewerRequest)} {newerId:N}";
        if (this.Session.TextPath is not null)
        {
            string text = $"{this.Translate(ModHealthViewerTranslationKeys.TextArtifact)} {this.Session.TextPath}";
            return this.Session.JsonPath is null
                ? text
                : $"{text} · {this.Translate(ModHealthViewerTranslationKeys.JsonArtifact)} {this.Session.JsonPath}";
        }
        if (this.Session.JsonPath is not null)
            return $"{this.Translate(ModHealthViewerTranslationKeys.JsonArtifact)} {this.Session.JsonPath}";
        return $"{this.Translate(ModHealthViewerTranslationKeys.Request)} {this.Session.RequestId:N}";
    }

    private int MeasurePreferredNavigationWidth()
    {
        float width = 0;
        for (int index = 0; index < ModHealthViewerLayout.SectionCount; index++)
            width = Math.Max(width, Game1.smallFont.MeasureString(this.Translate(ModHealthViewerTranslationKeys.Section((ModHealthViewerSection)index))).X);
        return (int)Math.Ceiling(width) + 16;
    }

    private int MeasurePreferredActionWidth()
    {
        float width = 0;
        for (int index = 0; index < this.Session.AvailableActionCount; index++)
            width = Math.Max(width, Game1.smallFont.MeasureString(this.Translate(ModHealthViewerTranslationKeys.Action(this.Session.GetAvailableAction(index)))).X);
        return (int)Math.Ceiling(width) + 16;
    }

    private static void Fill(SpriteBatch b, ModHealthLayoutRectangle bounds, Color color)
    {
        if (bounds.Width > 0 && bounds.Height > 0)
            b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height), color);
    }

    private static void DrawText(SpriteBatch b, string text, ModHealthLayoutRectangle bounds, Color color, int offsetX, int offsetY)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || string.IsNullOrEmpty(text))
            return;
        int availableWidth = Math.Max(0, bounds.Width - offsetX * 2);
        int availableHeight = bounds.Height - offsetY;
        if (availableWidth <= 0 || availableHeight < Game1.smallFont.LineSpacing)
            return;
        string visible = ModHealthViewerText.ClipToWidth(text, availableWidth, MeasureSmallText);
        if (visible.Length == 0)
            return;
        b.DrawString(Game1.smallFont, visible, new Vector2(bounds.X + offsetX, bounds.Y + offsetY), color);
    }

    private static void DrawCenteredText(SpriteBatch b, string text, ModHealthLayoutRectangle bounds, Color color)
    {
        string visible = ModHealthViewerText.ClipToWidth(text, Math.Max(0, bounds.Width - 8), MeasureSmallText);
        if (visible.Length == 0 || bounds.Height < Game1.smallFont.LineSpacing)
            return;
        Vector2 size = Game1.smallFont.MeasureString(visible);
        b.DrawString(Game1.smallFont, visible, new Vector2(bounds.X + Math.Max(4, (bounds.Width - size.X) / 2), bounds.Y + Math.Max(0, (bounds.Height - size.Y) / 2)), color);
    }

    private static float MeasureSmallText(string text) => Game1.smallFont.MeasureString(text).X;

    private static Color StateColor(ModHealthPreparedReportState state)
    {
        return state switch
        {
            ModHealthPreparedReportState.Saved or ModHealthPreparedReportState.ReadyBeforeWrite => Color.LightGreen,
            ModHealthPreparedReportState.WriteFailed or ModHealthPreparedReportState.FailedBeforeModel or ModHealthPreparedReportState.Rejected => Color.OrangeRed,
            ModHealthPreparedReportState.Superseded or ModHealthPreparedReportState.Canceled or ModHealthPreparedReportState.Disposed => Color.Orange,
            _ => Color.White
        };
    }

    private static Color RowColor(ModHealthViewerRowSeverity severity)
    {
        return severity switch
        {
            ModHealthViewerRowSeverity.Positive => Color.LightGreen,
            ModHealthViewerRowSeverity.Info => Color.LightBlue,
            ModHealthViewerRowSeverity.Warning => Color.Gold,
            ModHealthViewerRowSeverity.Error => Color.OrangeRed,
            _ => Color.White
        };
    }
}
