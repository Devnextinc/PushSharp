﻿using System;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using System.Net;
using PushSharp.Core;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PushSharp.Google.Gcm
{
    public class GcmServiceConnectionFactory : IServiceConnectionFactory<GcmNotification>
    {
        public GcmServiceConnectionFactory (GcmConfiguration configuration)
        {
            Configuration = configuration;
        }

        public GcmConfiguration Configuration { get; private set; }

        public IServiceConnection<GcmNotification> Create()
        {
            return new GcmServiceConnection (Configuration);
        }
    }

    public class GcmServiceBroker : ServiceBroker<GcmNotification>
    {
        public GcmServiceBroker (GcmConfiguration configuration) : base (new GcmServiceConnectionFactory (configuration))
        {
        }
    }

    public class GcmServiceConnection : IServiceConnection<GcmNotification>
    {
        public GcmServiceConnection (GcmConfiguration configuration)
        {
            Configuration = configuration;
        }

        public GcmConfiguration Configuration { get; private set; }

        readonly HttpClient http = new HttpClient ();

        public async Task Send (GcmNotification notification)
        {
            var json = notification.GetJson ();

            var content = new StringContent (json, System.Text.Encoding.UTF8, "application/json");
            http.DefaultRequestHeaders.UserAgent.Clear ();
            http.DefaultRequestHeaders.UserAgent.Add (new ProductInfoHeaderValue ("PushSharp", "3.0"));
            //http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue ("Authorization:", "key=" + Configuration.SenderAuthToken);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "key=" + Configuration.SenderAuthToken);

//            var req = new HttpRequestMessage (HttpMethod.Post, Configuration.GcmUrl);
//            req.Headers.try.Add ("Authorization", "key=" + Configuration.SenderAuthToken);
//            req.Content = content;

            //var response = await http.SendAsync (req);
            var response = await http.PostAsync (Configuration.GcmUrl, content);

            if (response.IsSuccessStatusCode) {
                await processResponseOk (response, notification);
            } else {
                var body = await response.Content.ReadAsStringAsync ();
                processResponseError (response, notification);
            }
        }

        async Task processResponseOk (HttpResponseMessage httpResponse, GcmNotification notification)
        {
            var multicastResult = new GcmMulticastResultException ();

            var result = new GcmResponse () {
                ResponseCode = GcmResponseCode.Ok,
                OriginalNotification = notification
            };

            var str = await httpResponse.Content.ReadAsStringAsync ();
            var json = JObject.Parse (str); 

            result.NumberOfCanonicalIds = json.Value<long> ("canonical_ids");
            result.NumberOfFailures = json.Value<long> ("failure");
            result.NumberOfSuccesses = json.Value<long> ("success");

            var jsonResults = json ["results"] as JArray ?? new JArray ();

            foreach (var r in jsonResults) {
                var msgResult = new GcmMessageResult ();

                msgResult.MessageId = r.Value<string> ("message_id");
                msgResult.CanonicalRegistrationId = r.Value<string> ("registration_id");
                msgResult.ResponseStatus = GcmResponseStatus.Ok;

                if (!string.IsNullOrEmpty (msgResult.CanonicalRegistrationId))
                    msgResult.ResponseStatus = GcmResponseStatus.CanonicalRegistrationId;
                else if (r ["error"] != null) {
                    var err = r.Value<string> ("error") ?? "";

                    msgResult.ResponseStatus = GetGcmResponseStatus (err);
                }

                result.Results.Add (msgResult);
            }

            int index = 0;

            //Loop through every result in the response
            // We will raise events for each individual result so that the consumer of the library
            // can deal with individual registrationid's for the notification
            foreach (var r in result.Results) {
                var singleResultNotification = GcmNotification.ForSingleResult (result, index);

                if (r.ResponseStatus == GcmResponseStatus.Ok) { // Success
                    multicastResult.Succeeded.Add (singleResultNotification);
                } else if (r.ResponseStatus == GcmResponseStatus.CanonicalRegistrationId) { //Need to swap reg id's
                    //Swap Registrations Id's
                    var newRegistrationId = r.CanonicalRegistrationId;
                    var oldRegistrationId = string.Empty;

                    if (singleResultNotification.RegistrationIds != null && singleResultNotification.RegistrationIds.Count > 0)
                        oldRegistrationId = singleResultNotification.RegistrationIds [0];

                    multicastResult.Failed.Add (singleResultNotification, new DeviceSubscriptonExpiredException {
                        OldSubscriptionId = oldRegistrationId,
                        NewSubscriptionId = newRegistrationId
                    });
                } else if (r.ResponseStatus == GcmResponseStatus.Unavailable) { // Unavailable
                    multicastResult.Failed.Add (singleResultNotification, new GcmConnectionException ("Unavailable Response Status"));
                } else if (r.ResponseStatus == GcmResponseStatus.NotRegistered) { //Bad registration Id
                    var oldRegistrationId = string.Empty;

                    if (singleResultNotification.RegistrationIds != null && singleResultNotification.RegistrationIds.Count > 0)
                        oldRegistrationId = singleResultNotification.RegistrationIds [0];

                    multicastResult.Failed.Add (singleResultNotification, new DeviceSubscriptonExpiredException { OldSubscriptionId = oldRegistrationId });
                } else {
                    multicastResult.Failed.Add (singleResultNotification, new GcmConnectionException ("Unknown Failure: " + r.ResponseStatus));
                }

                index++;
            }

            // If we only have 1 total result, it is not *multicast*, 
            if (multicastResult.Succeeded.Count + multicastResult.Failed.Count == 1) {
                // If not multicast, and succeeded, don't throw any errors!
                if (multicastResult.Succeeded.Count == 1)
                    return;
                // Otherwise, throw the one single failure we must have
                throw multicastResult.Failed.First ().Value;
            }

            // If we get here, we must have had a multicast message
            // throw if we had any failures at all (otherwise all must be successful, so throw no error
            if (multicastResult.Failed.Count > 0)
                throw multicastResult;
        }

        void processResponseError (HttpResponseMessage httpResponse, GcmNotification notification)
        {
            //401 bad auth token
            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException ("GCM Authorization Failed");

            if (httpResponse.StatusCode == HttpStatusCode.BadRequest)
                throw new GcmConnectionException ("HTTP 400 Bad Request");

            if ((int)httpResponse.StatusCode >= 500 && (int)httpResponse.StatusCode < 600) {
                //First try grabbing the retry-after header and parsing it.
                var retryAfterHeader = httpResponse.Headers.RetryAfter;

                if (retryAfterHeader != null && retryAfterHeader.Delta.HasValue) {
                    var retryAfter = retryAfterHeader.Delta.Value;
                    throw new RetryAfterException ("GCM Requested Backoff", DateTime.UtcNow + retryAfter);
                }                  
            }

            throw new GcmConnectionException ("GCM HTTP Error: " + httpResponse.StatusCode);           
        }

        static GcmResponseStatus GetGcmResponseStatus (string str)
        {
            var enumType = typeof(GcmResponseStatus);

            foreach (var name in Enum.GetNames (enumType)) {
                var enumMemberAttribute = ((EnumMemberAttribute[])enumType.GetField (name).GetCustomAttributes (typeof(EnumMemberAttribute), true)).Single ();

                if (enumMemberAttribute.Value.Equals (str, StringComparison.InvariantCultureIgnoreCase))
                    return (GcmResponseStatus)Enum.Parse (enumType, name);
            }

            //Default
            return GcmResponseStatus.Error;
        }
    }
}
