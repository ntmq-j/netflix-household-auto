using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

using MailKit;
using MailKit.Net.Imap;
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
        static readonly string[] HouseholdUrlHints =
        [
            "household",
            "update-household",
            "set-primary-location",
            ConfirmationUrlToken
        ];

        readonly ImapSettings imapSettings = imapSettings;
        readonly ILogger logger = logger;
        readonly ImapClient imapClient = new();

        DateTime lastConfirmationEmailDateTime = DateTime.UtcNow;

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

            IEnumerable<MimeMessage> emails = RetrieveRecentEmails();

            foreach (MimeMessage email in emails)
            {
                if (!IsPotentialNetflixHouseholdEmail(email))
                {
                    continue;
                }

                DateTime emailDateTime = email.Date.UtcDateTime;

                if (emailDateTime <= lastConfirmationEmailDateTime)
                {
                    continue;
                }

                // Move cursor forward as soon as we pick up a new matching email
                // so one malformed message does not get retried forever.
                lastConfirmationEmailDateTime = emailDateTime;

                string confirmationUrl = ExtractConfirmationUrlFromEmail(email);

                if (!string.IsNullOrWhiteSpace(confirmationUrl))
                {
                    return confirmationUrl;
                }

                logger.Error(
                    MyOperation.ListenForConfirmationRequests,
                    OperationStatus.Failure,
                    $"Matched a Netflix household email but could not extract a valid confirmation URL. Subject: {email.Subject}");
            }

            return null;
        }

        private string ExtractConfirmationUrlFromEmail(MimeMessage email)
        {
            string content = $"{email.HtmlBody}\n{email.TextBody}";

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

        private IEnumerable<MimeMessage> RetrieveRecentEmails()
        {
            var inbox = imapClient.Inbox;

            if (!inbox.IsOpen)
            {
                inbox.Open(FolderAccess.ReadOnly);
            }

            IList<MimeMessage> emails = [];

            for(int i = inbox.Count - 1; i >= 0; i--)
            {
                var email = inbox.GetMessage(i);

                if ((DateTime.UtcNow - email.Date.UtcDateTime).TotalSeconds > imapSettings.MaxEmailAge)
                {
                    break;
                }

                emails.Add(email);
            }

            return emails;
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

            if (!subject.Contains(HouseholdUpdateSubject, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (MailboxAddress mailbox in email.From.Mailboxes)
            {
                string[] parts = mailbox.Address?.Split('@');

                if (parts is null || parts.Length != 2)
                {
                    continue;
                }

                if (IsTrustedNetflixDomain(parts[1]))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsTrustedNetflixDomain(string domain)
            => !string.IsNullOrWhiteSpace(domain) &&
               (domain.Equals("netflix.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith(".netflix.com", StringComparison.OrdinalIgnoreCase));
    }
}
