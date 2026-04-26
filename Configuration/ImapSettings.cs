namespace NetflixHouseholdConfirmator.Configuration
{
    public sealed class ImapSettings
    {
        public string Server { get; set; }

        public int Port { get; set; }

        public string Username { get; set; }

        public string Password { get; set; }

        public int MaxEmailAge { get; set; }

        public int MaxSearchResultsToFetch { get; set; } = 20;

        public string Folder { get; set; }
    }
}
