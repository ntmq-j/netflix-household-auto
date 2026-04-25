namespace NetflixHouseholdConfirmator.Configuration
{
    public sealed class BotSettings
    {
        public int PageLoadTimeout { get; set; }

        public int PollIntervalSeconds { get; set; } = 5;

        public int ErrorRetryDelaySeconds { get; set; } = 15;
    }
}
