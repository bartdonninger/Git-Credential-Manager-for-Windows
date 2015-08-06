﻿using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace Microsoft.TeamFoundation.Authentication
{
    /// <summary>
    /// Base functionality for performing authentication operations against Visual Studio Online.
    /// </summary>
    public abstract class BaseVsoAuthentication : BaseAuthentication
    {
        public const string DefaultResource = "499b84ac-1321-427f-aa17-267ca6975798";
        public const string DefaultClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1";
        public const string RedirectUrl = "urn:ietf:wg:oauth:2.0:oob";

        protected const string AdalRefreshPrefx = "ada";

        private BaseVsoAuthentication(VsoTokenScope tokenScope, ICredentialStore personalAccessTokenStore)
        {
            if (tokenScope == null)
                throw new ArgumentNullException("scope", "The `scope` parameter is null or invalid.");
            if (personalAccessTokenStore == null)
                throw new ArgumentNullException("personalAccessTokenStore", "The `personalAccessTokenStore` paramter is null or invalid.");

            AdalTrace.TraceSource.Switch.Level = SourceLevels.Off;
            AdalTrace.LegacyTraceSwitch.Level = TraceLevel.Off;

            this.ClientId = DefaultClientId;
            this.Resource = DefaultResource;
            this.TokenScope = tokenScope;
            this.PersonalAccessTokenStore = personalAccessTokenStore;
            this.AdaRefreshTokenStore = new SecretStore(AdalRefreshPrefx);
            this.VsoAuthority = new VsoAzureAuthority();
        }
        /// <summary>
        /// Invoked by a derived classes implementation. Allows custom back-end implementatoins to be used.
        /// </summary>
        /// <param name="tokenScope">The desired scope of the acquired personal access token(s).</param>
        /// <param name="personalAccessTokenStore">The secret store for acquired pesonal access token(s).</param>
        /// <param name="adaRefreshTokenStore">The secret store for acquired Azure refresh token(s).</param>
        protected BaseVsoAuthentication(
            VsoTokenScope tokenScope,
            ICredentialStore personalAccessTokenStore,
            ITokenStore adaRefreshTokenStore = null)
            : this(tokenScope, personalAccessTokenStore)
        {
            this.AdaRefreshTokenStore = adaRefreshTokenStore ?? this.AdaRefreshTokenStore;
            this.VsoAdalTokenCache = new VsoAdalTokenCache();
            this.VsoIdeTokenCache = new TokenRegistry();
        }
        internal BaseVsoAuthentication(
            ICredentialStore personalAccessTokenStore,
            ITokenStore adaRefreshTokenStore,
            ITokenStore vsoIdeTokenCache,
            IVsoAuthority vsoAuthority)
            : this(VsoTokenScope.ProfileRead, personalAccessTokenStore)
        {
            Debug.Assert(adaRefreshTokenStore != null, "The adaRefreshTokenStore paramter is null or invalid.");
            Debug.Assert(vsoIdeTokenCache != null, "The vsoIdeTokenCache paramter is null or invalid.");
            Debug.Assert(vsoAuthority != null, "The vsoAuthority paramter is null or invalid.");

            this.AdaRefreshTokenStore = adaRefreshTokenStore;
            this.VsoIdeTokenCache = vsoIdeTokenCache;
            this.VsoAuthority = vsoAuthority;
            this.VsoAdalTokenCache = TokenCache.DefaultShared;
        }

        /// <summary>
        /// The application client identity by which access will be requested.
        /// </summary>
        public readonly string ClientId;
        /// <summary>
        /// The Azure resource for which access will be requested.
        /// </summary>
        public readonly string Resource;
        /// <summary>
        /// The desired scope of the authentication token to be requested.
        /// </summary>
        public readonly VsoTokenScope TokenScope;

        internal readonly TokenCache VsoAdalTokenCache;
        internal readonly ITokenStore VsoIdeTokenCache;

        internal ICredentialStore PersonalAccessTokenStore { get; set; }
        internal ITokenStore AdaRefreshTokenStore { get; set; }
        internal IVsoAuthority VsoAuthority { get; set; }
        internal Guid TenantId { get; set; }

        /// <summary>
        /// Deletes a set of stored credentials by their target resource.
        /// </summary>
        /// <param name="targetUri">The 'key' by which to identify credentials.</param>
        public override void DeleteCredentials(Uri targetUri)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            Trace.WriteLine("BaseVsoAuthentication::DeleteCredentials");

            Credential credentials = null;
            Token token = null;
            if (this.PersonalAccessTokenStore.ReadCredentials(targetUri, out credentials))
            {
                this.PersonalAccessTokenStore.DeleteCredentials(targetUri);
            }
            else if (this.AdaRefreshTokenStore.ReadToken(targetUri, out token))
            {
                this.AdaRefreshTokenStore.DeleteToken(targetUri);
            }
        }

        /// <summary>
        /// Attempts to get a set of credentials from storage by their target resource.
        /// </summary>
        /// <param name="targetUri">The 'key' by which to identify credentials.</param>
        /// <param name="credentials">Credentials associated with the URI is successful; null 
        /// otherwise.</param>
        /// <returns>True if successful; false otherwise.</returns>
        public override bool GetCredentials(Uri targetUri, out Credential credentials)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            Trace.WriteLine("BaseVsoAuthentication::GetCredentials");

            if (this.PersonalAccessTokenStore.ReadCredentials(targetUri, out credentials))
            {
                Trace.WriteLine("   successfully retrieved stored credentials, updating credential cache");
            }

            return credentials != null;
        }

        /// <summary>
        /// Attempts to generate a new personal access token (credentials) via use of a stored 
        /// Azure refresh token, identitifed by the target resource.
        /// </summary>
        /// <param name="targetUri">The 'key' by which to identify the refresh token.</param>
        /// <param name="requireCompactToken">Generates a compact token if true; generates a self 
        /// describing token if false.</param>
        /// <returns>True if successful; false otherwise.</returns>
        public async Task<bool> RefreshCredentials(Uri targetUri, bool requireCompactToken)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            Trace.WriteLine("BaseVsoAuthentication::RefreshCredentials");

            try
            {
                TokenPair tokens = null;

                Token refreshToken = null;
                // attempt to read from the local store
                if (this.AdaRefreshTokenStore.ReadToken(targetUri, out refreshToken))
                {
                    if ((tokens = await this.VsoAuthority.AcquireTokenByRefreshTokenAsync(targetUri, this.ClientId, this.Resource, refreshToken)) != null)
                    {
                        Trace.WriteLine("   Azure token found in primary cache.");

                        this.TenantId = tokens.AccessToken.TenantId;

                        return await this.GeneratePersonalAccessToken(targetUri, tokens.AccessToken, requireCompactToken);
                    }
                }

                Token federatedAuthToken;
                // attempt to utilize any fedauth tokens captured by the IDE
                if (this.VsoIdeTokenCache.ReadToken(targetUri, out federatedAuthToken))
                {
                    Trace.WriteLine("   federated auth token found in IDE cache.");

                    return await this.GeneratePersonalAccessToken(targetUri, federatedAuthToken, requireCompactToken);
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }

            Trace.WriteLine("   failed to refresh credentials.");
            return false;
        }

        /// <summary>
        /// Validates that a set of credentials grants access to the target resource.
        /// </summary>
        /// <param name="targetUri">The target resource to validate against.</param>
        /// <param name="credentials">The credentials to validate</param>
        /// <returns>True if successful; false otherwise.</returns>
        public async Task<bool> ValidateCredentials(Uri targetUri, Credential credentials)
        {
            Trace.WriteLine("BaseVsoAuthentication::ValidateCredentials");

            return await this.VsoAuthority.ValidateCredentials(targetUri, credentials);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="targetUri">The target resource for which to acquire the personal access 
        /// token for.</param>
        /// <param name="accessToken">Azure Directory access token with privilages to grant access 
        /// to the target resource.</param>
        /// <param name="requestCompactToken">Generates a compact token if true; generates a self 
        /// describing token if false.</param>
        /// <returns>True if successful; false otherwise.</returns>
        protected async Task<bool> GeneratePersonalAccessToken(Uri targetUri, Token accessToken, bool requestCompactToken)
        {
            Debug.Assert(targetUri != null, "The targetUri parameter is null");
            Debug.Assert(accessToken != null, "The accessToken parameter is null");

            Trace.WriteLine("BaseVsoAuthentication::GeneratePersonalAccessToken");

            Token personalAccessToken;
            if ((personalAccessToken = await this.VsoAuthority.GeneratePersonalAccessToken(targetUri, accessToken, TokenScope, requestCompactToken)) != null)
            {
                this.PersonalAccessTokenStore.WriteCredentials(targetUri, (Credential)personalAccessToken);
            }

            return personalAccessToken != null;
        }

        /// <summary>
        /// Stores an Azure Directory refresh token.
        /// </summary>
        /// <param name="targetUri">The 'key' by which to identify the token.</param>
        /// <param name="refreshToken">The token to be stored.</param>
        protected void StoreRefreshToken(Uri targetUri, Token refreshToken)
        {
            Debug.Assert(targetUri != null, "The targetUri parameter is null");
            Debug.Assert(refreshToken != null, "The refreshToken parameter is null");

            Trace.WriteLine("BaseVsoAuthentication::StoreRefreshToken");

            this.AdaRefreshTokenStore.WriteToken(targetUri, refreshToken);
        }

        /// <summary>
        /// Creates a new authentication broker based for the specified resource.
        /// </summary>
        /// <param name="targetUri">The resource for which authentication is being requested.</param>
        /// <param name="scope">The scope of the access being requested.</param>
        /// <param name="personalAccessTokenStore">Storage container for personal access token secrets.</param>
        /// <param name="adaRefreshTokenStore">Storage container for Azure access token secrets.</param>
        /// <returns></returns>
        public static BaseAuthentication GetAuthentication(
            Uri targetUri,
            VsoTokenScope scope,
            ICredentialStore personalAccessTokenStore,
            ITokenStore adaRefreshTokenStore = null)
        {
            Trace.WriteLine("BaseVsoAuthentication::DetectAuthority");

            Guid tenantId;
            if (DetectAuthority(targetUri, out tenantId))
            {
                // empty Guid is MSA, anything else is AAD
                if (tenantId == Guid.Empty)
                {
                    Trace.WriteLine("   MSA authority detected");
                    return new VsoMsaAuthentication(scope, personalAccessTokenStore, adaRefreshTokenStore);
                }
                else
                {
                    Trace.WriteLine("   AAD authority for tenant '" + tenantId + "' detected");
                    return new VsoAadAuthentication(tenantId, scope, personalAccessTokenStore, adaRefreshTokenStore);
                }
            }

            // if all else fails, fallback to basic authentication
            return new BasicAuthentication(personalAccessTokenStore);
        }
    }
}