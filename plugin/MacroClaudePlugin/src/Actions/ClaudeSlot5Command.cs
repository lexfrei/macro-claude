using System;

namespace Loupedeck.MacroClaudePlugin.Actions;

public sealed class ClaudeSlot5Command : SlotCommandBase
{
    public ClaudeSlot5Command()
        : base(displayNumber: 5)
    {
    }

    protected override Int32 SlotIndex => 4;
}
