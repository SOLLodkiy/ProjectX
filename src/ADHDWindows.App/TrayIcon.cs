using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Hud;

namespace ADHDWindows.App;

/// <summary>Значок в трее.</summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly int[] OpacityChoices = [100, 75, 50];

    private readonly NotifyIcon _icon;

    public TrayIcon(TextCatalog texts, string iconPath, bool developerMode, TrayActions actions)
    {
        var menu = new ContextMenuStrip();
        if (developerMode)
        {
            menu.Items.Add(new ToolStripMenuItem(texts.Get("tray.devMode")) { Enabled = false });
            if (actions.OnShowcase is { } onShowcase)
            {
                menu.Items.Add(texts.Get("tray.showcase"), null, (_, _) => onShowcase());
            }

            menu.Items.Add(texts.Get("tray.grantCoins"), null, (_, _) => actions.OnGrantCoins());
            menu.Items.Add(texts.Get("tray.recoveryProbe"), null, (_, _) => actions.OnRecoveryProbe());
            menu.Items.Add(new ToolStripSeparator());
        }

        if (actions.Hud is { } hud)
        {
            menu.Items.Add(BuildHudMenu(texts, hud));
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add(texts.Get("tray.locks"), null, (_, _) => actions.OnLocks());
        menu.Items.Add(new ToolStripMenuItem(texts.Get("tray.emergency", actions.EmergencyHotkey)) { Enabled = false });
        menu.Items.Add(texts.Get("tray.exit"), null, (_, _) => actions.OnExit());

        _icon = new NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new Icon(iconPath, SystemInformation.SmallIconSize) : SystemIcons.Application,
            Text = texts.Get("app.title"),
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private static ToolStripMenuItem BuildHudMenu(TextCatalog texts, HudSettings hud)
    {
        var scale = new ToolStripMenuItem(texts.Get("tray.hud.scale"));
        for (var value = HudSettings.MinScale; value <= HudSettings.MaxScale; value++)
        {
            scale.DropDownItems.Add(Choice($"×{value}", value, () => hud.Scale, chosen => hud.Scale = chosen));
        }

        var opacity = new ToolStripMenuItem(texts.Get("tray.hud.opacity"));
        foreach (var value in OpacityChoices)
        {
            opacity.DropDownItems.Add(Choice($"{value}%", value, () => hud.Opacity, chosen => hud.Opacity = chosen));
        }

        return new ToolStripMenuItem(
            texts.Get("tray.hud"),
            null,
            Toggle(texts.Get("tray.hud.visible"), () => hud.Visible, value => hud.Visible = value),
            Toggle(texts.Get("tray.hud.flyouts"), () => hud.ShowFlyouts, value => hud.ShowFlyouts = value),
            Toggle(texts.Get("tray.hud.hideFullscreen"), () => hud.HideInFullscreen, value => hud.HideInFullscreen = value),
            scale,
            opacity);
    }

    private static ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = get(), CheckOnClick = true };
        item.CheckedChanged += (_, _) => set(item.Checked);
        return item;
    }

    private static ToolStripMenuItem Choice(string text, int value, Func<int> get, Action<int> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = get() == value };
        item.Click += (_, _) =>
        {
            set(value);
            foreach (ToolStripMenuItem sibling in item.Owner!.Items)
            {
                sibling.Checked = sibling == item;
            }
        };
        return item;
    }
}

/// <summary>Что умеет меню трея. Значение null убирает пункт.</summary>
internal sealed record TrayActions(
    HudSettings? Hud,
    Action? OnShowcase,
    Action OnGrantCoins,
    Action OnRecoveryProbe,
    Action OnLocks,
    Action OnExit,
    string EmergencyHotkey);
