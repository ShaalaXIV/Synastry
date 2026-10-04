namespace EmoteLink.TestPartner;

/// <summary>The test partner's own relay connection joining and leaving a room (kept out of the
/// plugin's unsafe context, which can't await).</summary>
internal static class TestPartnerRoom
{
    public static async Task Join(AnimationSyncService connection, string url, string code, string name, IReadOnlyList<string> fingerprints)
    {
        if (!connection.IsConnected) await connection.ConnectAsync(url);
        if (connection.IsInRoom) await connection.LeaveRoomAsync();
        await connection.JoinRoomAsync(code, name, fingerprints);
        await connection.SetFreeUseAsync(true);
    }

    public static async Task Leave(AnimationSyncService connection)
    {
        try
        {
            if (connection.IsInRoom) await connection.LeaveRoomAsync();
            await connection.DisconnectAsync();
        }
        catch
        {
            // The relay may already be gone.
        }
    }
}
