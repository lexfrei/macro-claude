using System;

namespace Loupedeck.MacroClaudePlugin.Actions;

public sealed class ClaudeSlot4Command : SlotCommandBase
{
    public ClaudeSlot4Command()
        : base(displayNumber: 4)
    {
    }

    protected override Int32 SlotIndex => 3;
}
