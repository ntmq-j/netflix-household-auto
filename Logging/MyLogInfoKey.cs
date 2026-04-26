using NuciLog.Core;

namespace NetflixHouseholdConfirmator.Logging
{
    public sealed class MyLogInfoKey : LogInfoKey
    {
        MyLogInfoKey(string name)
            : base(name)
        {

        }

        public static LogInfoKey Server => new MyLogInfoKey(nameof(Server));

        public static LogInfoKey Port => new MyLogInfoKey(nameof(Port));

        public static LogInfoKey Username => new MyLogInfoKey(nameof(Username));

        public static LogInfoKey Password => new MyLogInfoKey(nameof(Password));

        public static LogInfoKey MaxAge => new MyLogInfoKey(nameof(MaxAge));

        public static LogInfoKey ConfirmationStatus => new MyLogInfoKey(nameof(ConfirmationStatus));

        public static LogInfoKey RequestedByProfile => new MyLogInfoKey(nameof(RequestedByProfile));

        public static LogInfoKey RequestedFromDevice => new MyLogInfoKey(nameof(RequestedFromDevice));

        public static LogInfoKey RequestedAt => new MyLogInfoKey(nameof(RequestedAt));

        public static LogInfoKey PageTitle => new MyLogInfoKey(nameof(PageTitle));

        public static LogInfoKey PageHeading => new MyLogInfoKey(nameof(PageHeading));

        public static LogInfoKey CurrentUrl => new MyLogInfoKey(nameof(CurrentUrl));

        public static LogInfoKey EmailDate => new MyLogInfoKey(nameof(EmailDate));

        public static LogInfoKey EmailIdentity => new MyLogInfoKey(nameof(EmailIdentity));

        public static LogInfoKey InboxCount => new MyLogInfoKey(nameof(InboxCount));
    }
}
