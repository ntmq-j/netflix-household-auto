using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NetflixHouseholdConfirmator.Logging;
using NuciLog.Core;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace NetflixHouseholdConfirmator.Service.Processors
{
    public sealed class NetflixProcessor(
        IWebDriver webDriver,
        ILogger logger) : INetflixProcessor
    {
        readonly IWebDriver webDriver = webDriver;
        readonly ILogger logger = logger;

        public bool ConfirmHousehold(string confirmationUrl)
        {
            if (!IsValidNetflixConfirmationUrl(confirmationUrl))
            {
                logger.Error(
                    MyOperation.HouseholdConfirmation,
                    OperationStatus.Failure,
                    "The extracted household confirmation URL is invalid or not from a trusted Netflix domain.");

                return false;
            }

            logger.Info(
                MyOperation.HouseholdConfirmation,
                OperationStatus.Started,
                "Starting the household confirmation process.");

            try
            {
                webDriver.Navigate().GoToUrl(confirmationUrl);

                By requestApprovalButtonSelector = By.XPath(
                    @"//button[contains(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'yes') and contains(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'this was me')]");
                By confirmUpdateButtonSelector = By.XPath(
                    @"//button[@data-uia='set-primary-location-action' or contains(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'confirm update')]");
                By locationDetailsSelector = By.XPath(@"//div[@data-uia='location-details']");

                PageSnapshot initialSnapshot = CapturePageSnapshot(
                    requestApprovalButtonSelector,
                    confirmUpdateButtonSelector,
                    locationDetailsSelector);

                logger.Info(
                    MyOperation.HouseholdConfirmation,
                    OperationStatus.InProgress,
                    "Loaded the Netflix household confirmation page.",
                    initialSnapshot.ToLogInfos());

                PageState firstPageState = WaitForActionOrTerminal(
                    requestApprovalButtonSelector,
                    confirmUpdateButtonSelector);

                if (IsTerminalStatus(firstPageState.Snapshot.Status))
                {
                    LogTerminalPageStatus(firstPageState.Snapshot);

                    return IsSuccessfulTerminalStatus(firstPageState.Snapshot.Status);
                }

                if (ElementMatches(firstPageState.ActionElement, requestApprovalButtonSelector))
                {
                    Click(firstPageState.ActionElement);
                }

                PageState finalConfirmationState = WaitForActionOrTerminal(confirmUpdateButtonSelector);
                PageSnapshot finalConfirmationSnapshot = finalConfirmationState.Snapshot;

                if (IsTerminalStatus(finalConfirmationSnapshot.Status))
                {
                    LogTerminalPageStatus(finalConfirmationSnapshot);

                    return IsSuccessfulTerminalStatus(finalConfirmationSnapshot.Status);
                }

                Click(finalConfirmationState.ActionElement);

                WaitForConfirmationToFinish(confirmUpdateButtonSelector);

                PageSnapshot resultSnapshot = CapturePageSnapshot(
                    requestApprovalButtonSelector,
                    confirmUpdateButtonSelector,
                    locationDetailsSelector);

                if (TryFindVisibleElement(confirmUpdateButtonSelector) is not null ||
                    resultSnapshot.Status is ConfirmationPageStatus.AwaitingFinalConfirmation or
                    ConfirmationPageStatus.AwaitingRequestApproval or
                    ConfirmationPageStatus.Unknown)
                {
                    LogFailurePageStatus(
                        resultSnapshot,
                        "The final Netflix confirmation status could not be verified after clicking the confirmation button.");

                    return false;
                }

                logger.Info(
                    MyOperation.HouseholdConfirmation,
                    OperationStatus.Success,
                    "The household was successfully confirmed.",
                    resultSnapshot.ToLogInfos());

                return true;
            }
            catch (Exception exception)
            {
                PageSnapshot snapshot = CapturePageSnapshot();

                logger.Error(
                    MyOperation.HouseholdConfirmation,
                    OperationStatus.Failure,
                    "An error has occurred while confirming the household.",
                    exception,
                    snapshot.ToLogInfos());

                LogFailurePageStatus(
                    snapshot,
                    "The Netflix household confirmation failed with the page status shown in this log entry.");

                return false;
            }
        }

        IWebElement WaitForAnyElementToBeVisible(params By[] selectors)
        {
            WebDriverWait wait = CreateWait();

            return wait.Until(_ =>
            {
                foreach (By selector in selectors)
                {
                    IWebElement element = TryFindVisibleElement(selector);

                    if (element is not null)
                    {
                        return element;
                    }
                }

                return null;
            });
        }

        IWebElement WaitForElementToBeVisible(By selector)
            => CreateWait().Until(_ => TryFindVisibleElement(selector));

        PageState WaitForActionOrTerminal(params By[] actionSelectors)
        {
            WebDriverWait wait = CreateWait();

            return wait.Until(_ =>
            {
                PageSnapshot snapshot = CapturePageSnapshot(actionSelectors);

                if (IsTerminalStatus(snapshot.Status))
                {
                    return new PageState(null, snapshot);
                }

                foreach (By actionSelector in actionSelectors)
                {
                    IWebElement actionElement = TryFindVisibleElement(actionSelector);

                    if (actionElement is not null)
                    {
                        return new PageState(actionElement, snapshot);
                    }
                }

                return null;
            });
        }

        void WaitForConfirmationToFinish(By confirmUpdateButtonSelector)
        {
            WebDriverWait wait = CreateWait();

            wait.Until(_ => TryFindVisibleElement(confirmUpdateButtonSelector) is null);
        }

        void Click(IWebElement element)
        {
            try
            {
                element.Click();
            }
            catch (ElementClickInterceptedException)
            {
                ((IJavaScriptExecutor)webDriver).ExecuteScript("arguments[0].click();", element);
            }
        }

        bool ElementMatches(IWebElement element, By selector)
        {
            try
            {
                return webDriver.FindElement(selector).Equals(element);
            }
            catch (NoSuchElementException)
            {
                return false;
            }
            catch (StaleElementReferenceException)
            {
                return false;
            }
        }

        IWebElement TryFindVisibleElement(By selector)
        {
            try
            {
                IWebElement element = webDriver.FindElement(selector);

                return element.Displayed ? element : null;
            }
            catch (NoSuchElementException)
            {
                return null;
            }
            catch (StaleElementReferenceException)
            {
                return null;
            }
        }

        WebDriverWait CreateWait()
            => new(webDriver, TimeSpan.FromSeconds(30));

        PageSnapshot CapturePageSnapshot(params By[] knownSelectors)
        {
            string title = SafeGet(() => webDriver.Title);
            string currentUrl = RedactUrl(SafeGet(() => webDriver.Url));
            string bodyText = SafeGet(() => webDriver.FindElement(By.TagName("body")).Text);
            string heading = SafeGet(() => webDriver.FindElements(By.XPath("//h1|//h2"))
                .FirstOrDefault(element => element.Displayed)?.Text);

            RequestMetadata requestMetadata = ExtractRequestMetadata(bodyText);
            ConfirmationPageStatus status = DetectPageStatus($"{title} {heading} {bodyText}", knownSelectors);

            return new PageSnapshot(
                status,
                requestMetadata.Profile,
                requestMetadata.Device,
                requestMetadata.RequestedAt,
                title,
                heading,
                currentUrl);
        }

        ConfirmationPageStatus DetectPageStatus(string bodyText, params By[] knownSelectors)
        {
            string normalisedBody = NormaliseWhitespace(bodyText).ToLowerInvariant();

            if (knownSelectors.Any(selector => TryFindVisibleElement(selector) is not null &&
                                               SelectorContainsText(selector, "yes", "this was me")))
            {
                return ConfirmationPageStatus.AwaitingRequestApproval;
            }

            if (knownSelectors.Any(selector => TryFindVisibleElement(selector) is not null &&
                                               SelectorContainsText(selector, "confirm update")))
            {
                return ConfirmationPageStatus.AwaitingFinalConfirmation;
            }

            if (normalisedBody.Contains("confirm update", StringComparison.Ordinal))
            {
                return ConfirmationPageStatus.AwaitingFinalConfirmation;
            }

            if (normalisedBody.Contains("yes, this was me", StringComparison.Ordinal))
            {
                return ConfirmationPageStatus.AwaitingRequestApproval;
            }

            if (normalisedBody.Contains("already", StringComparison.Ordinal) &&
                (normalisedBody.Contains("confirmed", StringComparison.Ordinal) ||
                 normalisedBody.Contains("updated", StringComparison.Ordinal)))
            {
                return ConfirmationPageStatus.AlreadyConfirmed;
            }

            if (normalisedBody.Contains("expired", StringComparison.Ordinal) ||
                normalisedBody.Contains("no longer valid", StringComparison.Ordinal))
            {
                return ConfirmationPageStatus.LinkExpired;
            }

            if (normalisedBody.Contains("sign in", StringComparison.Ordinal) ||
                normalisedBody.Contains("sign into", StringComparison.Ordinal))
            {
                return ConfirmationPageStatus.RequiresSignIn;
            }

            if (normalisedBody.Contains("household has been updated", StringComparison.Ordinal) ||
                normalisedBody.Contains("you've updated your netflix household", StringComparison.Ordinal) ||
                normalisedBody.Contains("you’ve updated your netflix household", StringComparison.Ordinal) ||
                normalisedBody.Contains("successfully updated", StringComparison.Ordinal) ||
                normalisedBody.Contains("was successfully confirmed", StringComparison.Ordinal))
            {
                return ConfirmationPageStatus.Confirmed;
            }

            if (normalisedBody.Contains("something went wrong", StringComparison.Ordinal) ||
                normalisedBody.Contains("sorry", StringComparison.Ordinal) &&
                normalisedBody.Contains("error", StringComparison.Ordinal))
            {
                return ConfirmationPageStatus.NetflixError;
            }

            return ConfirmationPageStatus.Unknown;
        }

        void LogTerminalPageStatus(PageSnapshot snapshot)
        {
            if (IsSuccessfulTerminalStatus(snapshot.Status))
            {
                logger.Info(
                    MyOperation.HouseholdConfirmation,
                    OperationStatus.Success,
                    $"Netflix returned terminal confirmation status: {snapshot.Status}.",
                    snapshot.ToLogInfos());

                return;
            }

            logger.Error(
                MyOperation.HouseholdConfirmation,
                OperationStatus.Failure,
                $"Netflix returned terminal confirmation status: {snapshot.Status}.",
                snapshot.ToLogInfos());
        }

        void LogFailurePageStatus(PageSnapshot snapshot, string message)
        {
            logger.Error(
                MyOperation.HouseholdConfirmation,
                OperationStatus.Failure,
                message,
                snapshot.ToLogInfos());
        }

        bool SelectorContainsText(By selector, params string[] expectedTexts)
        {
            IWebElement element = TryFindVisibleElement(selector);

            if (element is null)
            {
                return false;
            }

            string text = NormaliseWhitespace(element.Text).ToLowerInvariant();

            return expectedTexts.All(expectedText => text.Contains(expectedText, StringComparison.OrdinalIgnoreCase));
        }

        static RequestMetadata ExtractRequestMetadata(string bodyText)
        {
            string text = NormaliseWhitespace(bodyText);

            string device = MatchValue(text, @"NETFLIX HOUSEHOLD\s+Your\s+(?<value>.+?)\s+Requested by");
            string profile = MatchValue(text, @"Requested by\s+(?<value>.+?)\s+(?:from|at)\s+");
            string requestedAt = MatchValue(text, @"Requested by\s+.+?\s+at\s+(?<value>.+?)(?:\s+Updating|\s+If this|\s+Keep your|\s*$)");

            if (string.IsNullOrWhiteSpace(requestedAt))
            {
                requestedAt = MatchValue(text, @"Requested by\s+.+?\s+from\s+.+?\s+on\s+(?<value>.+?)(?:\s+\*|\s+Link expires|\s+Keep your|\s*$)");
            }

            if (string.IsNullOrWhiteSpace(device))
            {
                device = MatchValue(text, @"Requested by\s+.+?\s+from\s+(?<value>.+?)\s+on\s+");
            }

            return new RequestMetadata(
                NullIfEmpty(profile),
                NullIfEmpty(RemoveLeadingArticle(device)),
                NullIfEmpty(requestedAt));
        }

        static string RemoveLeadingArticle(string value)
            => string.IsNullOrWhiteSpace(value)
                ? value
                : Regex.Replace(value, @"^(a|an|the)\s+", string.Empty, RegexOptions.IgnoreCase).Trim();

        static string MatchValue(string text, string pattern)
        {
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);

            return match.Success ? NormaliseWhitespace(match.Groups["value"].Value) : null;
        }

        static string RedactUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                return "[unparseable-url]";
            }

            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        }

        static string SafeGet(Func<string> valueFactory)
        {
            try
            {
                return valueFactory();
            }
            catch (WebDriverException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        static string NormaliseWhitespace(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : Regex.Replace(value, @"\s+", " ").Trim();

        static string NullIfEmpty(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value;

        static bool IsTerminalStatus(ConfirmationPageStatus status)
            => status is ConfirmationPageStatus.AlreadyConfirmed or
                ConfirmationPageStatus.Confirmed or
                ConfirmationPageStatus.LinkExpired or
                ConfirmationPageStatus.RequiresSignIn or
                ConfirmationPageStatus.NetflixError;

        static bool IsSuccessfulTerminalStatus(ConfirmationPageStatus status)
            => status is ConfirmationPageStatus.AlreadyConfirmed or ConfirmationPageStatus.Confirmed;

        static bool IsValidNetflixConfirmationUrl(string confirmationUrl)
        {
            if (string.IsNullOrWhiteSpace(confirmationUrl))
            {
                return false;
            }

            if (!Uri.TryCreate(confirmationUrl, UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return IsTrustedNetflixDomain(uri.Host);
        }

        static bool IsTrustedNetflixDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                return false;
            }

            return domain.Equals("netflix.com", StringComparison.OrdinalIgnoreCase) ||
                   domain.EndsWith(".netflix.com", StringComparison.OrdinalIgnoreCase);
        }

        sealed record RequestMetadata(
            string Profile,
            string Device,
            string RequestedAt);

        sealed record PageSnapshot(
            ConfirmationPageStatus Status,
            string RequestedByProfile,
            string RequestedFromDevice,
            string RequestedAt,
            string PageTitle,
            string PageHeading,
            string CurrentUrl)
        {
            public IEnumerable<LogInfo> ToLogInfos()
            {
                if (Status != ConfirmationPageStatus.Unknown)
                {
                    yield return new(MyLogInfoKey.ConfirmationStatus, Status);
                }

                if (!string.IsNullOrWhiteSpace(RequestedByProfile))
                {
                    yield return new(MyLogInfoKey.RequestedByProfile, RequestedByProfile);
                }

                if (!string.IsNullOrWhiteSpace(RequestedFromDevice))
                {
                    yield return new(MyLogInfoKey.RequestedFromDevice, RequestedFromDevice);
                }

                if (!string.IsNullOrWhiteSpace(RequestedAt))
                {
                    yield return new(MyLogInfoKey.RequestedAt, RequestedAt);
                }

                if (!string.IsNullOrWhiteSpace(PageTitle))
                {
                    yield return new(MyLogInfoKey.PageTitle, PageTitle);
                }

                if (!string.IsNullOrWhiteSpace(PageHeading))
                {
                    yield return new(MyLogInfoKey.PageHeading, PageHeading);
                }

                if (!string.IsNullOrWhiteSpace(CurrentUrl))
                {
                    yield return new(MyLogInfoKey.CurrentUrl, CurrentUrl);
                }
            }
        }

        sealed record PageState(
            IWebElement ActionElement,
            PageSnapshot Snapshot);

        enum ConfirmationPageStatus
        {
            Unknown,
            AwaitingRequestApproval,
            AwaitingFinalConfirmation,
            Confirmed,
            AlreadyConfirmed,
            LinkExpired,
            RequiresSignIn,
            NetflixError
        }
    }
}
