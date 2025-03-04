﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using Microsoft.Identity.Client.Cache.Items;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.TelemetryCore.Internal.Events;
using Microsoft.Identity.Client.Utils;

namespace Microsoft.Identity.Client.Internal.Requests
{
    internal class OnBehalfOfRequest : RequestBase
    {
        private readonly AcquireTokenOnBehalfOfParameters _onBehalfOfParameters;
        private string _ccsRoutingHint;

        public OnBehalfOfRequest(
            IServiceBundle serviceBundle,
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenOnBehalfOfParameters onBehalfOfParameters)
            : base(serviceBundle, authenticationRequestParameters, onBehalfOfParameters)
        {
            _onBehalfOfParameters = onBehalfOfParameters;
        }

        protected override async Task<AuthenticationResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (AuthenticationRequestParameters.Scope == null || AuthenticationRequestParameters.Scope.Count == 0)
            {
                throw new MsalClientException(
                    MsalError.ScopesRequired,
                    MsalErrorMessage.ScopesRequired);
            }

            await ResolveAuthorityAsync().ConfigureAwait(false);
            MsalAccessTokenCacheItem cachedAccessToken = null;
            var logger = AuthenticationRequestParameters.RequestContext.Logger;
            AuthenticationResult authResult = null;

            CacheRefreshReason cacheInfoTelemetry = CacheRefreshReason.NotApplicable;
            if (!_onBehalfOfParameters.ForceRefresh && string.IsNullOrEmpty(AuthenticationRequestParameters.Claims))
            {
                // look for access token in the cache first.
                // no access token is found, then it means token does not exist
                // or new assertion has been passed. 
                // Look for a refresh token, if refresh token is found perform refresh token flow.
                // If a refresh token is not found, then it means refresh token does not exist or new assertion has been passed.
                // Fetch new access token for OBO
                using (logger.LogBlockDuration("[OBO Request] Looking in the cache for an access token"))
                {
                    cachedAccessToken = await CacheManager.FindAccessTokenAsync().ConfigureAwait(false);
                }

                if (cachedAccessToken != null)
                {
                    var cachedIdToken = await CacheManager.GetIdTokenCacheItemAsync(cachedAccessToken).ConfigureAwait(false);
                    var account = await CacheManager.GetAccountAssociatedWithAccessTokenAsync(cachedAccessToken).ConfigureAwait(false);

                    logger.Info(
                        "[OBO Request] Found a valid access token in the cache. ID token also found? " + (cachedIdToken != null));

                    AuthenticationRequestParameters.RequestContext.ApiEvent.IsAccessTokenCacheHit = true;

                    Metrics.IncrementTotalAccessTokensFromCache();
                    authResult = new AuthenticationResult(
                                                            cachedAccessToken,
                                                            cachedIdToken,
                                                            AuthenticationRequestParameters.AuthenticationScheme,
                                                            AuthenticationRequestParameters.RequestContext.CorrelationId,
                                                            TokenSource.Cache,
                                                            AuthenticationRequestParameters.RequestContext.ApiEvent,
                                                            account);
                }
                else
                {
                    if (AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo != CacheRefreshReason.Expired)
                    {
                        cacheInfoTelemetry = CacheRefreshReason.NoCachedAccessToken;
                    }
                }
            }
            else
            {
                logger.Info("[OBO Request] Skipped looking for an Access Token in the cache because ForceRefresh or Claims were set. ");
                cacheInfoTelemetry = CacheRefreshReason.ForceRefreshOrClaims;
            }

            if (AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo == CacheRefreshReason.NotApplicable)
            {
                AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo = cacheInfoTelemetry;
            }

            // No AT in the cache or AT needs to be refreshed
            try
            {
                if (cachedAccessToken == null)
                {
                    authResult = await RefreshRtOrFetchNewAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var shouldRefresh = SilentRequestHelper.NeedsRefresh(cachedAccessToken);

                    // may fire a request to get a new token in the background
                    if (shouldRefresh)
                    {
                        AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo = CacheRefreshReason.ProactivelyRefreshed;

                        SilentRequestHelper.ProcessFetchInBackground(
                        cachedAccessToken,
                        () => RefreshRtOrFetchNewAccessTokenAsync(cancellationToken), logger);
                    }
                }

                return authResult;
            }
            catch (MsalServiceException e)
            {
                return await HandleTokenRefreshErrorAsync(e, cachedAccessToken).ConfigureAwait(false);
            }
        }

        private async Task<AuthenticationResult> RefreshRtOrFetchNewAccessTokenAsync(CancellationToken cancellationToken)
        {
            var logger = AuthenticationRequestParameters.RequestContext.Logger;
            
            if (IsLongRunningObo())
            {
                AuthenticationRequestParameters.RequestContext.Logger.Info("[OBO request] Long-running OBO flow, trying to refresh using an refresh token flow.");

                // Look for a refresh token
                MsalRefreshTokenCacheItem cachedRefreshToken = await CacheManager.FindRefreshTokenAsync().ConfigureAwait(false);

                // If a refresh token is not found, fetch a new access token
                if (cachedRefreshToken != null)
                {
                    logger.Info("[OBO request] Found a refresh token");
                    if (!string.IsNullOrEmpty(cachedRefreshToken.RawClientInfo))
                    {
                        var clientInfo = ClientInfo.CreateFromJson(cachedRefreshToken.RawClientInfo);
                        
                        _ccsRoutingHint = CoreHelpers.GetCcsClientInfoHint(
                            clientInfo.UniqueObjectIdentifier,
                            clientInfo.UniqueTenantIdentifier);
                    }
                    else
                    {
                        logger.Info("[OBO request] No client info associated with RT. This is OBO for a Service Principal.");
                    }

                    var msalTokenResponse = await SilentRequestHelper.RefreshAccessTokenAsync(cachedRefreshToken, this, AuthenticationRequestParameters, cancellationToken)
                    .ConfigureAwait(false);

                    return await CacheTokenResponseAndCreateAuthenticationResultAsync(msalTokenResponse).ConfigureAwait(false);
                }

                if (AcquireTokenInLongRunningOboWasCalled())
                {
                    AuthenticationRequestParameters.RequestContext.Logger.Error("[OBO request] AcquireTokenInLongRunningProcess was called and no access or refresh tokens were found in the cache.");
                    throw new MsalClientException(MsalError.OboCacheKeyNotInCacheError, MsalErrorMessage.OboCacheKeyNotInCache);
                }

                AuthenticationRequestParameters.RequestContext.Logger.Info("[OBO request] No Refresh Token was found in the cache. Fetching OBO token from ESTS");
            }
            else
            {
                logger.Info("[OBO request] Normal OBO flow, skipping to fetching access token via OBO flow.");
            }

            return await FetchNewAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }

        // Returns whether AcquireTokenInLongRunningProcess was called (user assertion is null in this case)
        private bool AcquireTokenInLongRunningOboWasCalled()
        {
            return AuthenticationRequestParameters.UserAssertion == null &&
                !string.IsNullOrEmpty(AuthenticationRequestParameters.LongRunningOboCacheKey);
        }

        private async Task<AuthenticationResult> FetchNewAccessTokenAsync(CancellationToken cancellationToken)
        {
            var msalTokenResponse = await SendTokenRequestAsync(GetBodyParameters(), cancellationToken).ConfigureAwait(false);

            // We always retrieve a refresh token in OBO but we don't want to cache it for normal OBO flow, only for long-running OBO
            if (!IsLongRunningObo())
            {
                msalTokenResponse.RefreshToken = null;
            }

            if (msalTokenResponse.ClientInfo is null &&
                AuthenticationRequestParameters.AuthorityInfo.AuthorityType != AuthorityType.Adfs)
            {
                var logger = AuthenticationRequestParameters.RequestContext.Logger;
                logger.Info("[OBO request] This is an on behalf of request for a service principal as no client info returned in the token response.");
            }

            return await CacheTokenResponseAndCreateAuthenticationResultAsync(msalTokenResponse).ConfigureAwait(false);
        }

        private Dictionary<string, string> GetBodyParameters()
        {
            var dict = new Dictionary<string, string>
            {
                [OAuth2Parameter.ClientInfo] = "1",
                [OAuth2Parameter.GrantType] = _onBehalfOfParameters.UserAssertion.AssertionType,
                [OAuth2Parameter.Assertion] = _onBehalfOfParameters.UserAssertion.Assertion,
                [OAuth2Parameter.RequestedTokenUse] = OAuth2RequestedTokenUse.OnBehalfOf
            };
            return dict;
        }

        protected override KeyValuePair<string, string>? GetCcsHeader(IDictionary<string, string> additionalBodyParameters)
        {
            if (string.IsNullOrEmpty(_ccsRoutingHint))
            {
                return null;
            }

            return new KeyValuePair<string, string>(Constants.CcsRoutingHintHeader, _ccsRoutingHint);
        }

        private bool IsLongRunningObo()
        {
            return !string.IsNullOrEmpty(AuthenticationRequestParameters.LongRunningOboCacheKey);
        }
    }
}
