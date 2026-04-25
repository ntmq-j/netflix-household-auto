namespace NetflixHouseholdConfirmator.Service.Processors
{
    public interface INetflixProcessor
    {
        bool ConfirmHousehold(string confirmationUrl);
    }
}
