using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ADHDWindows.Core.Data;

namespace ADHDWindows.App;

/// <summary>Значок в трее.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(TextCatalog texts, string iconPath, Action? onShowcase, bool developerMode, Action onExit)
    {
        var menu = new ContextMenuStrip();
        if (developerMode)
        {
            menu.Items.Add(new ToolStripMenuItem(texts.Get("tray.devMode")) { Enabled = false });
            if (onShowcase is not null)
            {
                menu.Items.Add(texts.Get("tray.showcase"), null, (_, _) => onShowcase());
            }

            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add(texts.Get("tray.exit"), null, (_, _) => onExit());

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
}
