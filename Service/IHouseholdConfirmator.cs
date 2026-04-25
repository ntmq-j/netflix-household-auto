using System.Threading;

namespace NetflixHouseholdConfirmator.Service
{
    public interface IHouseholdConfirmator
    {
        void ConfirmIncomingHouseholdUpdateRequests(CancellationToken cancellationToken = default);
    }
}
