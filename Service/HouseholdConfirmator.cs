using System;
using System.Threading;
using NetflixHouseholdConfirmator.Logging;
using NetflixHouseholdConfirmator.Configuration;
using NetflixHouseholdConfirmator.Service.Processors;
using NuciLog.Core;

namespace NetflixHouseholdConfirmator.Service
{
    public class HouseholdConfirmator(
        BotSettings botSettings,
        IEmailProcessor emailProcessor,
        INetflixProcessor netflixProcessor,
        ILogger logger)
        : IHouseholdConfirmator
    {
        readonly BotSettings botSettings = botSettings;
        readonly IEmailProcessor emailProcessor = emailProcessor;
        readonly INetflixProcessor netflixProcessor = netflixProcessor;
        readonly ILogger logger = logger;

        public void ConfirmIncomingHouseholdUpdateRequests(CancellationToken cancellationToken = default)
        {
            emailProcessor.LogIn();

            logger.Info(
                MyOperation.ListenForConfirmationRequests,
                OperationStatus.Started,
                "Listening for incoming household update requests.");

            try
            {
                while(!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        string confirmationUrl = emailProcessor.GetHouseholdConfirmationUrl();

                        if (confirmationUrl is not null)
                        {
                            bool hasConfirmed = netflixProcessor.ConfirmHousehold(confirmationUrl);

                            if (!hasConfirmed)
                            {
                                logger.Error(
                                    MyOperation.HouseholdConfirmation,
                                    OperationStatus.Failure,
                                    "A confirmation URL was found, but the household confirmation did not succeed.");
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        logger.Error(
                            MyOperation.ListenForConfirmationRequests,
                            OperationStatus.Failure,
                            "Failed to process a polling cycle. Retrying after delay.",
                            exception);

                        WaitForDelay(botSettings.ErrorRetryDelaySeconds, cancellationToken);
                        continue;
                    }

                    WaitForDelay(botSettings.PollIntervalSeconds, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                logger.Error(
                    MyOperation.ListenForConfirmationRequests,
                    OperationStatus.Failure,
                    exception);

                throw;
            }
            finally
            {
                emailProcessor.LogOut();
            }
        }

        static void WaitForDelay(int delaySeconds, CancellationToken cancellationToken)
        {
            if (delaySeconds <= 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(delaySeconds));
        }
    }
}
