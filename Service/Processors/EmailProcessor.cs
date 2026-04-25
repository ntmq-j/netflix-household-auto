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

                string confirmationUrl = ExtractConfirmationUrlFromEmail(email);

                if (!string.IsNullOrWhiteSpace(confirmationUrl))
                {
                    lastConfirmationEmailDateTime = emailDateTime;
                    return confirmationUrl;
                }
            }

            return null;
        }

        private string ExtractConfirmationUrlFromEmail(MimeMessage email)
        {
            string content = email.HtmlBody ?? email.TextBody;

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            MatchCollection matches = Regex.Matches(
                content,
                "href\\s*=\\s*[\"'](?<url>https://[^\"']+)[\"']",
                RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                string candidate = WebUtility.HtmlDecode(match.Groups["url"].Value);

                if (!candidate.Contains(ConfirmationUrlToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri))
                {
                    continue;
                }

                if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsTrustedNetflixDomain(uri.Host))
                {
                    continue;
                }

                return uri.ToString();
            }

            return null;
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
