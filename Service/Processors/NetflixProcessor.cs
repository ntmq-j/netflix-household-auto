using System;
using NetflixHouseholdConfirmator.Logging;
using NuciLog.Core;
using NuciWeb;
using NuciWeb.Automation;

namespace NetflixHouseholdConfirmator.Service.Processors
{
    public sealed class NetflixProcessor(
        IWebProcessor webProcessor,
        ILogger logger) : INetflixProcessor
    {
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
                webProcessor.GoToUrl(confirmationUrl);

                string confirmButtonSelector = Select.ByXPath(@"//button[@data-uia='set-primary-location-action']");
                string locationDetailsSelector = Select.ByXPath(@"//div[@data-uia='location-details']");

                webProcessor.WaitForAnyElementToBeVisible(confirmButtonSelector, locationDetailsSelector);

                if (!webProcessor.IsElementVisible(locationDetailsSelector))
                {
                    webProcessor.Click(confirmButtonSelector);
                    webProcessor.Wait(5000);
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
