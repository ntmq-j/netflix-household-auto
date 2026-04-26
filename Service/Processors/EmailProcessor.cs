using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using NuciLog.Core;

using NetflixHouseholdConfirmator.Configuration;
using NetflixHouseholdConfirmator.Logging;

namespace NetflixHouseholdConfirmator.Service.Processors
{
    public sealed class EmailProcessor(
        ImapSettings imapSettings,
        ILogger logger) : IEmailProcessor
    {
        const string HouseholdUpdateSubject = "How to update your Netflix Household";
        const string ConfirmationUrlToken = "UPDATE_HOUSEHOLD_REQUESTED_OTP_CTA";
        const int MaxFallbackMessagesToScan = 100;
        const int MaxSearchResultsToFetch = 50;
        static readonly IDictionary<string, int> EnglishMonthNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["January"] = 1,
            ["February"] = 2,
            ["March"] = 3,
            ["April"] = 4,
            ["May"] = 5,
            ["June"] = 6,
            ["July"] = 7,
            ["August"] = 8,
            ["September"] = 9,
            ["October"] = 10,
            ["November"] = 11,
            ["December"] = 12
        };
        static readonly string[] HouseholdUrlHints =
        [
            "household",
            "update-household",
            "set-primary-location",
            "primary-location",
            "location",
            "confirm",
            "verify",
            "otp",
            ConfirmationUrlToken
        ];

        readonly ImapSettings imapSettings = imapSettings;
        readonly ILogger logger = logger;
        readonly ImapClient imapClient = new();

        readonly ISet<string> processedEmailIds = new HashSet<string>();

        public void LogIn()
        {
            IEnumerable<LogInfo> logInfos =
            [
                new(MyLogInfoKey.Server, imapSettings.Server),
                new(MyLogInfoKey.Port, imapSettings.Port)
            ];

            logger.Info(
                MyOperation.EmailLogIn,
                OperationStatus.Started,
                "Connecting to the IMAP server.",
                logInfos);

            try
            {
                if (!imapClient.IsConnected)
                {
                    imapClient.Connect(imapSettings.Server, imapSettings.Port, true);
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    MyOperation.EmailLogIn,
                    OperationStatus.Failure,
                    "Failed to connect to the IMAP server.",
                    ex,
                    logInfos);

                throw;
            }

            logInfos = logInfos.Append(new(MyLogInfoKey.Username, imapSettings.Username));

            logger.Info(
                MyOperation.EmailLogIn,
                OperationStatus.InProgress,
                "Authenticating on the IMAP server.",
                logInfos);

            try
            {
                if (!imapClient.IsAuthenticated)
                {
                    imapClient.Authenticate(imapSettings.Username, imapSettings.Password);
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    MyOperation.EmailLogIn,
                    OperationStatus.Failure,
                    "Failed to authenticate on the IMAP server.",
                    ex,
                    logInfos);

                throw;
            }

            logger.Info(
                MyOperation.EmailLogIn,
                OperationStatus.Success,
                "Logged into the IMAP server.",
                logInfos);
        }

        public void LogOut()
        {
            IEnumerable<LogInfo> logInfos =
            [
                new(MyLogInfoKey.Server, imapSettings.Server),
                new(MyLogInfoKey.Port, imapSettings.Port),
                new(MyLogInfoKey.Username, imapSettings.Username)
            ];

            logger.Info(
                MyOperation.EmailLogOut,
                OperationStatus.Started,
                "Disconnecting from the IMAP server.",
                logInfos);

            try
            {
                if (imapClient.IsConnected)
                {
                    imapClient.Disconnect(true);
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    MyOperation.EmailLogOut,
                    OperationStatus.Failure,
                    "Failed to disconnect from the IMAP server.",
                    ex,
                    logInfos);

                throw;
            }

            imapClient.Dispose();

            logger.Info(
                MyOperation.EmailLogOut,
                OperationStatus.Success,
                "Logged out of the IMAP server.",
                logInfos);
        }

        public string GetHouseholdConfirmationUrl()
        {
            EnsureConnectedAndAuthenticated();

            RetrievedEmails retrievedEmails = RetrieveRecentEmails();
            List<MimeMessage> emails = retrievedEmails.Emails.ToList();

            logger.Info(
                MyOperation.ListenForConfirmationRequests,
                OperationStatus.InProgress,
                $"Retrieved {emails.Count} Netflix candidate email(s) across IMAP folder(s): {retrievedEmails.FolderCounts}.",
                [
                    new(MyLogInfoKey.InboxCount, imapClient.Inbox.Count),
                    new(MyLogInfoKey.FolderCounts, retrievedEmails.FolderCounts)
                ]);

            List<HouseholdEmailCandidate> householdEmailCandidates = emails
                .Where(IsPotentialNetflixHouseholdEmail)
                .Select(CreateHouseholdEmailCandidate)
                .Where(IsWithinConfiguredAgeWindow)
                .OrderByDescending(candidate => candidate.SortDate)
                .ThenByDescending(candidate => candidate.Email.Date.UtcDateTime)
                .ToList();

            logger.Info(
                MyOperation.ListenForConfirmationRequests,
                OperationStatus.InProgress,
                $"Matched {householdEmailCandidates.Count} Netflix household email candidate(s) inside the configured age window.");

            HouseholdEmailCandidate candidate = householdEmailCandidates.FirstOrDefault();

            if (candidate is null)
            {
                return null;
            }

            if (processedEmailIds.Contains(candidate.EmailIdentity))
            {
                return null;
            }

            processedEmailIds.Add(candidate.EmailIdentity);

            IEnumerable<LogInfo> logInfos =
            [
                new(MyLogInfoKey.EmailDate, candidate.Email.Date.ToString("O")),
                new(MyLogInfoKey.EmailIdentity, candidate.EmailIdentity)
            ];

            if (!string.IsNullOrWhiteSpace(candidate.RequestedAt))
            {
                logInfos = logInfos.Append(new(MyLogInfoKey.RequestedAt, candidate.RequestedAt));
            }

            logger.Info(
                MyOperation.ListenForConfirmationRequests,
                OperationStatus.InProgress,
                $"Found latest Netflix household email. Subject: {candidate.Email.Subject}",
                logInfos);

            if (!string.IsNullOrWhiteSpace(candidate.ConfirmationUrl))
            {
                logger.Info(
                    MyOperation.ListenForConfirmationRequests,
                    OperationStatus.Success,
                    "Extracted a Netflix household confirmation URL.",
                    logInfos);

                return candidate.ConfirmationUrl;
            }

            logger.Error(
                MyOperation.ListenForConfirmationRequests,
                OperationStatus.Failure,
                $"Matched the latest Netflix household email but could not extract a valid confirmation URL. Subject: {candidate.Email.Subject}",
                logInfos);

            return null;
        }

        HouseholdEmailCandidate CreateHouseholdEmailCandidate(MimeMessage email)
        {
            string content = GetEmailContent(email);
            string confirmationUrl = ExtractConfirmationUrlFromEmailContent(content);
            string requestedAt = ExtractRequestDateFromEmailContent(content);
            DateTimeOffset sortDate = TryParseNetflixRequestDate(requestedAt, email.Date.Year, out DateTimeOffset parsedRequestDate)
                ? parsedRequestDate
                : email.Date;
            string emailIdentity = GetEmailIdentity(email, confirmationUrl, requestedAt);

            return new HouseholdEmailCandidate(
                email,
                confirmationUrl,
                requestedAt,
                sortDate,
                emailIdentity);
        }

        private string ExtractConfirmationUrlFromEmail(MimeMessage email)
            => ExtractConfirmationUrlFromEmailContent(GetEmailContent(email));

        static string GetEmailContent(MimeMessage email)
            => $"{email.HtmlBody}\n{email.TextBody}";

        private string ExtractConfirmationUrlFromEmailContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            IEnumerable<string> candidates = ExtractUrlCandidates(content);
            List<string> trustedUrls = [];

            foreach (string candidate in candidates)
            {
                if (TryNormaliseNetflixUrl(candidate, out string netflixUrl))
                {
                    trustedUrls.Add(netflixUrl);
                }
            }

            if (trustedUrls.Count == 0)
            {
                return null;
            }

            string targetedUrl = trustedUrls.FirstOrDefault(IsLikelyHouseholdConfirmationUrl);

            return targetedUrl ?? trustedUrls.First();
        }

        static string ExtractRequestDateFromEmailContent(string content)
        {
            string text = NormaliseWhitespace(WebUtility.HtmlDecode(content));
            Match match = Regex.Match(
                text,
                @"request to update the Netflix household for your account on (?<date>\d{1,2}\s+[A-Za-z]+\s+\d{1,2}:\d{2}\s*(?:am|pm)?\s*GMT[+-]\d{1,2})",
                RegexOptions.IgnoreCase);

            return match.Success ? match.Groups["date"].Value.Trim() : null;
        }

        static bool TryParseNetflixRequestDate(string value, int fallbackYear, out DateTimeOffset requestDate)
        {
            requestDate = default;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            Match match = Regex.Match(
                value,
                @"^(?<day>\d{1,2})\s+(?<month>[A-Za-z]+)\s+(?<hour>\d{1,2}):(?<minute>\d{2})\s*(?<period>am|pm)?\s*GMT(?<offset>[+-]\d{1,2})$",
                RegexOptions.IgnoreCase);

            if (!match.Success ||
                !int.TryParse(match.Groups["day"].Value, out int day) ||
                !EnglishMonthNumbers.TryGetValue(match.Groups["month"].Value, out int month) ||
                !int.TryParse(match.Groups["hour"].Value, out int hour) ||
                !int.TryParse(match.Groups["minute"].Value, out int minute) ||
                !int.TryParse(match.Groups["offset"].Value, out int offsetHours))
            {
                return false;
            }

            string period = match.Groups["period"].Value;

            if (period.Equals("pm", StringComparison.OrdinalIgnoreCase) && hour < 12)
            {
                hour += 12;
            }
            else if (period.Equals("am", StringComparison.OrdinalIgnoreCase) && hour == 12)
            {
                hour = 0;
            }

            try
            {
                requestDate = new DateTimeOffset(
                    fallbackYear,
                    month,
                    day,
                    hour,
                    minute,
                    0,
                    TimeSpan.FromHours(offsetHours));

                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        static IEnumerable<string> ExtractUrlCandidates(string content)
        {
            MatchCollection hrefMatches = Regex.Matches(
                content,
                "href\\s*=\\s*[\"'](?<url>https://[^\"']+)[\"']",
                RegexOptions.IgnoreCase);

            foreach (Match match in hrefMatches)
            {
                string hrefValue = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();

                if (!string.IsNullOrWhiteSpace(hrefValue))
                {
                    yield return hrefValue;
                }
            }

            MatchCollection plainTextMatches = Regex.Matches(
                WebUtility.HtmlDecode(content),
                @"https://[^\s""'<>)]+",
                RegexOptions.IgnoreCase);

            foreach (Match match in plainTextMatches)
            {
                string textUrl = match.Value.Trim().TrimEnd('.', ',', ';', ')', ']', '"', '\'');

                if (!string.IsNullOrWhiteSpace(textUrl))
                {
                    yield return textUrl;
                }
            }
        }

        static bool TryNormaliseNetflixUrl(string candidateUrl, out string netflixUrl)
        {
            netflixUrl = null;

            if (string.IsNullOrWhiteSpace(candidateUrl))
            {
                return false;
            }

            if (TryExtractTrustedNetflixUrl(candidateUrl, out netflixUrl))
            {
                return true;
            }

            string decoded = WebUtility.UrlDecode(candidateUrl);

            if (!string.Equals(decoded, candidateUrl, StringComparison.Ordinal) &&
                TryExtractTrustedNetflixUrl(decoded, out netflixUrl))
            {
                return true;
            }

            return false;
        }

        static bool TryExtractTrustedNetflixUrl(string url, out string netflixUrl)
        {
            netflixUrl = null;

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsTrustedNetflixDomain(uri.Host))
            {
                netflixUrl = uri.ToString();
                return true;
            }

            string query = uri.Query.TrimStart('?');

            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            foreach (string queryPair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = queryPair.Split('=', 2);

                if (kv.Length != 2)
                {
                    continue;
                }

                string queryValue = WebUtility.UrlDecode(kv[1]);

                if (!Uri.TryCreate(queryValue, UriKind.Absolute, out Uri nestedUri))
                {
                    continue;
                }

                if (!nestedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsTrustedNetflixDomain(nestedUri.Host))
                {
                    continue;
                }

                netflixUrl = nestedUri.ToString();
                return true;
            }

            return false;
        }

        static bool IsLikelyHouseholdConfirmationUrl(string url)
        {
            string normalisedUrl = url.ToLowerInvariant();

            return HouseholdUrlHints.Any(hint => normalisedUrl.Contains(hint.ToLowerInvariant(), StringComparison.Ordinal));
        }

        private RetrievedEmails RetrieveRecentEmails()
        {
            List<MimeMessage> emails = [];
            List<string> folderCounts = [];
            HashSet<string> seenEmailIds = [];

            foreach (IMailFolder folder in GetScanFolders())
            {
                if (!folder.IsOpen)
                {
                    folder.Open(FolderAccess.ReadOnly);
                }

                RefreshFolder(folder);

                folderCounts.Add($"{folder.FullName}={folder.Count}");

                IList<MimeMessage> folderEmails = SearchNetflixEmails(folder).ToList();
                folderCounts.Add($"{folder.FullName}:search={folderEmails.Count}");

                AddUniqueEmails(emails, folderEmails, seenEmailIds);
            }

            return new RetrievedEmails(emails, string.Join(",", folderCounts));
        }

        IEnumerable<MimeMessage> SearchNetflixEmails(IMailFolder folder)
        {
            try
            {
                IList<UniqueId> emailIds = folder.Search(SearchQuery.SubjectContains("Netflix household"));

                return emailIds
                    .Reverse()
                    .Take(MaxSearchResultsToFetch)
                    .Select(emailId => folder.GetMessage(emailId))
                    .ToList();
            }
            catch (Exception exception)
            {
                logger.Error(
                    MyOperation.ListenForConfirmationRequests,
                    OperationStatus.Failure,
                    $"Failed to search folder {folder.FullName}. Falling back to the latest {MaxFallbackMessagesToScan} message(s).",
                    exception);

                return RetrieveLatestFolderEmails(folder);
            }
        }

        IEnumerable<MimeMessage> RetrieveLatestFolderEmails(IMailFolder folder)
        {
            int firstMessageIndex = Math.Max(0, folder.Count - MaxFallbackMessagesToScan);

            for(int i = folder.Count - 1; i >= firstMessageIndex; i--)
            {
                yield return folder.GetMessage(i);
            }
        }

        static void AddUniqueEmails(
            List<MimeMessage> emails,
            IEnumerable<MimeMessage> candidateEmails,
            ISet<string> seenEmailIds)
        {
            foreach (MimeMessage email in candidateEmails)
            {
                string emailKey = GetEmailScanKey(email);

                if (!seenEmailIds.Add(emailKey))
                {
                    continue;
                }

                emails.Add(email);
            }
        }

        IEnumerable<IMailFolder> GetScanFolders()
        {
            yield return imapClient.Inbox;

            IMailFolder allMailFolder = TryGetAllMailFolder();

            if (allMailFolder is not null &&
                !string.Equals(allMailFolder.FullName, imapClient.Inbox.FullName, StringComparison.OrdinalIgnoreCase))
            {
                yield return allMailFolder;
            }
        }

        IMailFolder TryGetAllMailFolder()
        {
            try
            {
                return imapClient.GetFolder(SpecialFolder.All);
            }
            catch (Exception)
            {
                return TryFindFolderByName("All Mail");
            }
        }

        IMailFolder TryFindFolderByName(string folderName)
        {
            foreach (FolderNamespace folderNamespace in imapClient.PersonalNamespaces)
            {
                IMailFolder rootFolder = imapClient.GetFolder(folderNamespace);
                IMailFolder matchedFolder = FindFolderByName(rootFolder, folderName);

                if (matchedFolder is not null)
                {
                    return matchedFolder;
                }
            }

            return null;
        }

        IMailFolder FindFolderByName(IMailFolder folder, string folderName)
        {
            if (folder.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase) ||
                folder.FullName.EndsWith($"/{folderName}", StringComparison.OrdinalIgnoreCase) ||
                folder.FullName.EndsWith($".{folderName}", StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }

            foreach (IMailFolder subfolder in folder.GetSubfolders(false))
            {
                IMailFolder matchedFolder = FindFolderByName(subfolder, folderName);

                if (matchedFolder is not null)
                {
                    return matchedFolder;
                }
            }

            return null;
        }

        void RefreshFolder(IMailFolder folder)
        {
            // Gmail can keep selected mailboxes open while new messages arrive in the
            // same conversation. NOOP/CHECK forces the folder summary/count to catch up.
            imapClient.NoOp();
            folder.Check();
        }

        void EnsureConnectedAndAuthenticated()
        {
            if (imapClient.IsConnected && imapClient.IsAuthenticated)
            {
                return;
            }

            LogIn();
        }

        static bool IsPotentialNetflixHouseholdEmail(MimeMessage email)
        {
            string subject = email.Subject ?? string.Empty;

            return subject.Contains(HouseholdUpdateSubject, StringComparison.OrdinalIgnoreCase);
        }

        static string GetEmailIdentity(MimeMessage email, string confirmationUrl, string requestedAt)
            => string.Join(
                "|",
                email.MessageId ?? string.Empty,
                email.Date.UtcDateTime.ToString("O"),
                email.Subject ?? string.Empty,
                string.Join(",", email.From.Mailboxes.Select(mailbox => mailbox.Address)),
                requestedAt ?? string.Empty,
                ComputeSha256(confirmationUrl ?? string.Empty));

        static string GetEmailScanKey(MimeMessage email)
        {
            if (!string.IsNullOrWhiteSpace(email.MessageId))
            {
                return email.MessageId;
            }

            return $"{email.Date.UtcDateTime:O}|{email.Subject}|{string.Join(",", email.From.Mailboxes.Select(mailbox => mailbox.Address))}";
        }

        static string ComputeSha256(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));

            return Convert.ToHexString(hash);
        }

        static bool IsTrustedNetflixDomain(string domain)
            => !string.IsNullOrWhiteSpace(domain) &&
               (domain.Equals("netflix.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith(".netflix.com", StringComparison.OrdinalIgnoreCase));

        bool IsWithinConfiguredAgeWindow(HouseholdEmailCandidate candidate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            double requestAgeSeconds = (now - candidate.SortDate.ToUniversalTime()).TotalSeconds;

            return requestAgeSeconds <= imapSettings.MaxEmailAge;
        }

        static string NormaliseWhitespace(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : Regex.Replace(value, @"\s+", " ").Trim();

        sealed record HouseholdEmailCandidate(
            MimeMessage Email,
            string ConfirmationUrl,
            string RequestedAt,
            DateTimeOffset SortDate,
            string EmailIdentity);

        sealed record RetrievedEmails(
            IEnumerable<MimeMessage> Emails,
            string FolderCounts);
    }
}
