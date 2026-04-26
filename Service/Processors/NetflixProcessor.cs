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

                By confirmButtonSelector = By.XPath(@"//button[@data-uia='set-primary-location-action']");
                By locationDetailsSelector = By.XPath(@"//div[@data-uia='location-details']");

                IWebElement visibleElement = WaitForAnyElementToBeVisible(
                    confirmButtonSelector,
                    locationDetailsSelector);

                if (TryFindVisibleElement(locationDetailsSelector) is null)
                {
                    visibleElement.Click();
                    WaitForElementToBeVisible(locationDetailsSelector);
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
