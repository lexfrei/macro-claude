using System;

namespace Loupedeck.MacroClaudePlugin.Actions;

public sealed class ClaudeSlot9Command : SlotCommandBase
{
    public ClaudeSlot9Command()
        : base(displayNumber: 9)
    {
    }

    protected override Int32 SlotIndex => 8;
}
