using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;

namespace BetterSaveSlots.Patches.ProfileScreen;

internal static partial class ProfileScreenPatches
{
    private enum ProfileActionKind
    {
        CopyPaste,
        Import,
        PreviousPage,
        NextPage
    }

    private sealed record ActionButtonInfo(
        WeakReference<NProfileScreen> Screen,
        int ProfileId,
        ProfileActionKind Kind);

    private sealed class ProfileScreenState
    {
        public int PageIndex { get; set; }

        public int? CopiedProfileId { get; set; }

        public bool DeferredLayoutUpdateQueued { get; set; }

        public List<NDeleteProfileButton> CopyPasteButtons { get; } = [];

        public List<NDeleteProfileButton> ImportButtons { get; } = [];

        public NDeleteProfileButton? PreviousPageButton { get; set; }

        public NDeleteProfileButton? NextPageButton { get; set; }
    }
}
