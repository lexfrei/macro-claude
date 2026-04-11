using System;

namespace Loupedeck.MacroClaudePlugin.Actions;

public sealed class ClaudeSlot6Command : SlotCommandBase
{
    public ClaudeSlot6Command()
        : base(displayNumber: 6)
    {
    }

    protected override Int32 SlotIndex => 5;
}
