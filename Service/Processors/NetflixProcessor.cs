using System;
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

                IWebElement firstStepElement = WaitForAnyElementToBeVisible(
                    requestApprovalButtonSelector,
                    confirmUpdateButtonSelector,
                    locationDetailsSelector);

                if (ElementMatches(firstStepElement, requestApprovalButtonSelector))
                {
                    Click(firstStepElement);
                }

                IWebElement confirmUpdateButton = WaitForElementToBeVisible(confirmUpdateButtonSelector);
                Click(confirmUpdateButton);

                WaitForConfirmationToFinish(confirmUpdateButtonSelector);

                if (TryFindVisibleElement(confirmUpdateButtonSelector) is not null)
                {
                    throw new WebDriverException("The final Netflix confirmation button is still visible after clicking it.");
                }

                logger.Info(
                    MyOperation.HouseholdConfirmation,
                    OperationStatus.Success,
                    "The household was successfully confirmed.");

                return true;
            }
            catch (Exception exception)
            {
                logger.Error(
                    MyOperation.HouseholdConfirmation,
                    OperationStatus.Failure,
                    "An error has occurred while confirming the household.",
                    exception);

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
    }
}
