using System;

namespace Loupedeck.MacroClaudePlugin.Actions;

public sealed class ClaudeSlot8Command : SlotCommandBase
{
    public ClaudeSlot8Command()
        : base(displayNumber: 8)
    {
    }

    protected override Int32 SlotIndex => 7;
}
