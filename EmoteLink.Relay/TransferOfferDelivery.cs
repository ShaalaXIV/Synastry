using Microsoft.AspNetCore.SignalR;

namespace EmoteLink.Relay;

internal static class TransferOfferDelivery
{
    public static Task DeliverAsync(
        IHubContext<AnimationHub> hub,
        TransferStore store,
        IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> offers,
        ILogger logger) =>
        Task.WhenAll(offers.Select(async item =>
        {
            if (!store.CanDeliverOffer(item.Offer.TransferId, item.ConnectionId)) return;
            try
            {
                // This intentionally does not use HttpContext.RequestAborted. Once the
                // checksum is accepted, a sender closing its request cannot suppress
                // offers to the other room members.
                await hub.Clients.Client(item.ConnectionId)
                    .SendAsync("ModTransferOffered", item.Offer, CancellationToken.None);
            }
            catch (Exception exception)
            {
                store.MarkOfferDeliveryFailed(item.Offer.TransferId, item.ConnectionId, exception.Message);
                logger.LogWarning(exception,
                    "Could not deliver transfer {TransferId} to one recipient; remaining offers continue",
                    item.Offer.TransferId);
            }
        }));
}
