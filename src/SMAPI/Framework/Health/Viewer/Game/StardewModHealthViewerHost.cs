using System;
using StardewModdingAPI.Enums;
using StardewValley;
using StardewValley.Menus;

namespace StardewModdingAPI.Framework.Health.Viewer.Game;

internal enum ModHealthViewerRootMenuKind
{
    None,
    Other
}

internal readonly record struct ModHealthViewerHostState(
    bool IsUnsafeTransition,
    ModHealthViewerRootMenuKind RootMenuKind,
    bool IsLocationTransition = false,
    bool IsFadeTransition = false,
    bool IsWarpTransition = false
);

/// <summary>Pure policy for deciding whether opening can preserve all current game/menu ownership.</summary>
internal static class ModHealthViewerHostPolicy
{
    public static bool CanOpen(ModHealthViewerHostState state, out string refusalTranslationKey)
    {
        if (state.IsUnsafeTransition || state.IsLocationTransition || state.IsFadeTransition || state.IsWarpTransition)
        {
            refusalTranslationKey = ModHealthViewerTranslationKeys.UnsafeState;
            return false;
        }
        if (state.RootMenuKind == ModHealthViewerRootMenuKind.None)
        {
            refusalTranslationKey = string.Empty;
            return true;
        }
        refusalTranslationKey = ModHealthViewerTranslationKeys.MenuBusy;
        return false;
    }
}

/// <summary>Attaches one viewer without replacing any menu owned by Stardew Valley or a mod.</summary>
internal sealed class StardewModHealthViewerHost : IModHealthViewerHost
{
    private ModHealthReportMenu? Menu;

    public bool CanOpen(out string refusalTranslationKey)
    {
        bool unsafeTransition = Context.IsSaving()
            || Game1.gameMode == Game1.loadingMode
            || Context.LoadStage is not (LoadStage.None or LoadStage.Ready)
            || Game1.currentMinigame is not null
            || Game1.dialogueUp
            || Game1.eventUp;
        bool locationTransition = Context.IsWorldReady && (Game1.currentLocation is null || Game1.player?.currentLocation is null);
        bool fadeTransition = Game1.fadeToBlack || Game1.fadeToBlackAlpha > 0;
        bool warpTransition = Game1.locationRequest is not null;

        IClickableMenu? active = Game1.activeClickableMenu;
        ModHealthViewerRootMenuKind menuKind = active switch
        {
            null => ModHealthViewerRootMenuKind.None,
            _ => ModHealthViewerRootMenuKind.Other
        };
        return ModHealthViewerHostPolicy.CanOpen(new(unsafeTransition, menuKind, locationTransition, fadeTransition, warpTransition), out refusalTranslationKey);
    }

    public bool TryOpen(ModHealthViewerSession session, ModHealthViewerController controller, Func<string, string> translate, out string refusalTranslationKey)
    {
        if (!this.CanOpen(out refusalTranslationKey))
            return false;

        ModHealthReportMenu menu = new(session, translate) { Controller = controller };
        // Recheck immediately before attachment and never replace or parent under a game/mod menu.
        if (Game1.activeClickableMenu is not null)
        {
            refusalTranslationKey = ModHealthViewerTranslationKeys.MenuBusy;
            return false;
        }

        Game1.activeClickableMenu = menu;
        this.Menu = menu;
        refusalTranslationKey = string.Empty;
        return true;
    }

    public bool Owns(Guid viewerInstanceId)
    {
        ModHealthReportMenu? menu = this.Menu;
        if (menu is null || menu.ViewerInstanceId != viewerInstanceId)
            return false;
        return ReferenceEquals(Game1.activeClickableMenu, menu) && menu.GetParentMenu() is null;
    }

    public void CloseOwned(Guid viewerInstanceId)
    {
        if (!this.Owns(viewerInstanceId))
            return;

        ModHealthReportMenu menu = this.Menu!;
        if (ReferenceEquals(Game1.activeClickableMenu, menu))
            Game1.activeClickableMenu = null;

        this.Release(viewerInstanceId);
    }

    public void Release(Guid viewerInstanceId)
    {
        if (this.Menu?.ViewerInstanceId == viewerInstanceId)
            this.Menu = null;
    }
}
