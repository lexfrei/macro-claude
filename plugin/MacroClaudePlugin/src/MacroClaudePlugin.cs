using System;

using Loupedeck.MacroClaudePlugin.Status;

namespace Loupedeck.MacroClaudePlugin;

// Plugin entry point. Owns the StatusReader lifecycle and routes
// session updates into slot assignments published on SlotBus.
public class MacroClaudePlugin : Plugin
{
    public override Boolean UsesApplicationApiOnly => true;
    public override Boolean HasNoApplication => true;

    private StatusReader? _statusReader;
    private SlotAssigner? _slotAssigner;

    public MacroClaudePlugin()
    {
        PluginLog.Init(this.Log);
        PluginResources.Init(this.Assembly);
    }

    public override void Load()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 27 = three full MX Creative Console profile pages. See
        // SessionStatusCommand.MaxSlots for rationale.
        this._slotAssigner = new SlotAssigner(maxSlots: 27);
        this._statusReader = new StatusReader(home);
        this._statusReader.SessionUpdated += this.OnSessionUpdated;
        this._statusReader.SessionRemoved += this.OnSessionRemoved;

        try
        {
            this._statusReader.Start();
            PluginLog.Info("macro-claude: StatusReader started");
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "macro-claude: StatusReader failed to start");
        }
    }

    public override void Unload()
    {
        if (this._statusReader != null)
        {
            this._statusReader.SessionUpdated -= this.OnSessionUpdated;
            this._statusReader.SessionRemoved -= this.OnSessionRemoved;
            this._statusReader.Dispose();
            this._statusReader = null;
        }
        this._slotAssigner = null;
    }

    private void OnSessionUpdated(Object? sender, SessionSnapshot snapshot)
    {
        var assigner = this._slotAssigner;
        if (assigner == null)
        {
            return;
        }
        var slot = assigner.Ensure(snapshot.SessionId);
        if (slot < 0)
        {
            // All slots occupied — ignore. A future version will scroll
            // to additional profile pages.
            return;
        }
        SlotBus.Publish(slot, snapshot);
    }

    private void OnSessionRemoved(Object? sender, String sessionId)
    {
        var assigner = this._slotAssigner;
        if (assigner == null)
        {
            return;
        }
        var slot = assigner.Release(sessionId);
        if (slot < 0)
        {
            return;
        }
        SlotBus.Publish(slot, null);
    }
}
