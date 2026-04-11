using System;

namespace Loupedeck.MacroClaudePlugin.Actions;

public sealed class ClaudeSlot2Command : SlotCommandBase
{
    public ClaudeSlot2Command()
        : base(displayNumber: 2)
    {
    }

    protected override Int32 SlotIndex => 1;
}
