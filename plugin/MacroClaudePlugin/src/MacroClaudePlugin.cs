using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

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
    private Guid _busToken = Guid.Empty;

    // Per-session memo of the last (slot, state) that OnSessionUpdated
    // actually logged. SessionLogDecision consults it to suppress the
    // "session → slot" verbose line when the update is a no-op repeat.
    private readonly ConcurrentDictionary<String, (Int32 Slot, SessionState State)> _lastLogged
        = new(StringComparer.Ordinal);

    public MacroClaudePlugin()
    {
        PluginLog.Init(this.Log);
        PluginResources.Init(this.Assembly);
    }

    public override void Load()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Nine = one MX Creative Console profile page, matching the
        // nine ClaudeSlot1Command..ClaudeSlot9Command classes in
        // src/Actions/. If you add more slot commands, bump this.
        this._slotAssigner = new SlotAssigner(maxSlots: SlotBus.ValidSlotCount);

        // Claim exclusive ownership of SlotBus. Any previous Plugin
        // instance that LPS failed to fully unload still has its old
        // token and will be silently rejected from publishing into
        // the bus. The snapshot store is wiped so stale zombie entries
        // never reach subscribers after a reload.
        this._busToken = SlotBus.AcquireOwnership();
        PluginLog.Info($"macro-claude: SlotBus ownership acquired by token {this._busToken}");

        this._statusReader = new StatusReader(home);
        this._statusReader.SessionUpdated += this.OnSessionUpdated;
        this._statusReader.SessionRemoved += this.OnSessionRemoved;

        // Start on a worker thread. Load() MUST return to LPS within
        // 10 seconds or the plugin gets marked as failed for the life
        // of the LPS process. StatusReader.Start() does an InitialScan
        // that emits SessionUpdated synchronously, each one ultimately
        // calls PluginDynamicCommand.ActionImageChanged which does an
        // IPC roundtrip to LPS — and LPS itself is blocked waiting for
        // Load() to return. That deadlocks until LPS gives up on the
        // Load timeout. Off-thread InitialScan breaks the cycle: Load
        // returns immediately, LPS finishes whatever it was doing, and
        // the InitialScan fires ActionImageChanged into a responsive
        // LPS a moment later.
        var reader = this._statusReader;
        _ = Task.Run(() =>
        {
            try
            {
                reader.Start();
                PluginLog.Info("macro-claude: StatusReader started");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "macro-claude: StatusReader failed to start");
            }
        });
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
        this._busToken = Guid.Empty;
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
            PluginLog.Warning($"macro-claude: no free slot for {snapshot.SessionId}");
            return;
        }

        // Suppress the verbose line when nothing render-relevant has
        // changed for this session. StatusReader re-emits every poll
        // tick with a fresh UpdatedAt stamp; without this filter the
        // plugin log grew at ~10 lines/sec per active session.
        var previous = this._lastLogged.TryGetValue(snapshot.SessionId, out var last)
            ? last
            : ((Int32, SessionState)?)null;
        if (SessionLogDecision.ShouldLog(previous, slot, snapshot.State))
        {
            PluginLog.Verbose(
                $"macro-claude: session {snapshot.SessionId} → slot {slot} state={snapshot.State} name={snapshot.ShortName}");
            this._lastLogged[snapshot.SessionId] = (slot, snapshot.State);
        }

        if (!SlotBus.Publish(this._busToken, slot, snapshot))
        {
            PluginLog.Warning(
                $"macro-claude: SlotBus.Publish rejected slot {slot} — token stale or index out of range");
        }
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
        this._lastLogged.TryRemove(sessionId, out _);
        PluginLog.Verbose($"macro-claude: session {sessionId} removed from slot {slot}");
        _ = SlotBus.Publish(this._busToken, slot, null);
    }
}
