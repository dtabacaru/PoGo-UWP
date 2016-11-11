using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
#if NETFX_CORE
using Windows.Networking.Connectivity;
#endif

namespace PokemonGo.RocketAPI.Helpers
{
    internal class RetryHandler : DelegatingHandler
    {
        private const int MaxRetries = 25;

        private readonly object _locker = new object();

        private bool _isNetworkAvailable = true;

        public RetryHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
#if NETFX_CORE
            NetworkInformation.NetworkStatusChanged += NetworkInformationOnNetworkStatusChanged;
#else
            NetworkChange.NetworkAvailabilityChanged += new NetworkAvailabilityChangedEventHandler(NetworkInformationOnNetworkStatusChanged);
#endif
            UpdateConnectionStatus();
        }

        private void UpdateConnectionStatus()
        {
            lock (_locker)
            {
#if NETFX_CORE
                var connectionProfile = NetworkInformation.GetInternetConnectionProfile();                
                _isNetworkAvailable = connectionProfile != null &&
                                      connectionProfile.GetNetworkConnectivityLevel() ==
                                      NetworkConnectivityLevel.InternetAccess;
#else
                // dtabacaru: couldn't find a great way to check for internet connectivity using .NET
                // http://stackoverflow.com/questions/2031824/what-is-the-best-way-to-check-for-internet-connectivity-using-net
                _isNetworkAvailable = CheckForInternetConnection();
#endif
                Monitor.PulseAll(_locker);
            }
        }
#if NETFX_CORE
        private void NetworkInformationOnNetworkStatusChanged(object sender)
#else
        private void NetworkInformationOnNetworkStatusChanged(object sender, EventArgs e)
#endif
        {
            UpdateConnectionStatus();                        
        }

#if !NETFX_CORE
        private bool CheckForInternetConnection()
        {
            try
            {
                using (var client = new WebClient())
                {
                    using (var stream = client.OpenRead("http://www.google.com"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
        }
#endif

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {                      

            for (var i = 0; i <= MaxRetries; i++)
            {
                try
                {

                    lock (_locker)
                    {
                        while (!_isNetworkAvailable)
                        {
                            Logger.Write($"{request.RequestUri} is waiting for Network to be available again.");
                            Monitor.Wait(_locker);
                        }
                    }

                    var response = await base.SendAsync(request, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.BadGateway ||
                        response.StatusCode == HttpStatusCode.InternalServerError)
                        throw new Exception(); //todo: proper implementation

                    return response;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[#{i} of {MaxRetries}] retry request {request.RequestUri} - Error: {ex}");
                    if (i < MaxRetries)
                    {
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }
                    throw;
                }
            }
            return null;
        }
    }
}