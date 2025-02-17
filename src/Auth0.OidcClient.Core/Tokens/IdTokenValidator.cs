﻿using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Auth0.OidcClient.Tokens
{
    /// <summary>
    /// Perform validation of a JWT ID token in compliance with specified <see cref="IdTokenRequirements"/>.
    /// </summary>
    internal class IdTokenValidator
    {
        private readonly AsymmetricSignatureVerifier assymetricSignatureVerifier;

        public IdTokenValidator(HttpMessageHandler backchannel = null)
        {
            assymetricSignatureVerifier = new AsymmetricSignatureVerifier(backchannel);
        }

        /// <summary>
        /// Assert that all the <see cref="IdTokenRequirements"/> are met by a JWT ID token for a given point in time.
        /// </summary>
        /// <param name="required"><see cref="IdTokenRequirements"/> that should be asserted.</param>
        /// <param name="rawIDToken">Raw ID token to assert requirements against.</param>
        /// <param name="pointInTime">Optional <see cref="DateTime"/> to act as "Now" in order to facilitate unit testing with static tokens.</param>
        /// <param name="signatureVerifier">Optional <see cref="ISignatureVerifier"/> to perform signature verification and token extraction. If unspecified
        /// <see cref="AsymmetricSignatureVerifier"/> is used against the <paramref name="required"/> Issuer.</param>
        /// <exception cref="IdTokenValidationException">Exception thrown if <paramref name="rawIDToken"/> fails to
        /// meet the requirements specified by <paramref name="required"/>.
        /// </exception>
        /// <returns><see cref="Task"/> that will complete when the token is validated.</returns>
        internal async Task AssertTokenMeetsRequirements(IdTokenRequirements required, string rawIDToken, DateTime? pointInTime = null, ISignatureVerifier signatureVerifier = null)
        {
            if (string.IsNullOrWhiteSpace(rawIDToken))
                throw new IdTokenValidationException("ID token is required but missing.");

            var token = DecodeToken(rawIDToken);

            // For now we want to support HS256 + ClientSecret as we just had a major release.
            // TODO: In the next major (v4.0) we should remove this condition as well as Auth0ClientOptions.ClientSecret
            if (token.SignatureAlgorithm != "HS256")
                (signatureVerifier ?? await assymetricSignatureVerifier.ForJwks(required.Issuer)).VerifySignature(rawIDToken);

            AssertTokenClaimsMeetRequirements(required, token, pointInTime ?? DateTime.Now);
        }

        private static JwtSecurityToken DecodeToken(string rawIDToken)
        {
            JwtSecurityToken decoded;
            try
            {
                decoded = new JwtSecurityTokenHandler().ReadJwtToken(rawIDToken);
            }
            catch (ArgumentException e)
            {
                throw new IdTokenValidationException("ID token could not be decoded.", e);
            }

            return decoded;
        }

        /// <summary>
        /// Assert that all the claims within a <see cref="JwtSecurityToken"/> meet the specified <see cref="IdTokenRequirements"/> for a given point in time.
        /// </summary>
        /// <param name="required"><see cref="IdTokenRequirements"/> that should be asserted.</param>
        /// <param name="token"><see cref="JwtSecurityToken"/> to assert requirements against.</param>
        /// <param name="pointInTime"><see cref="DateTime"/> to act as "Now" when asserting time-based claims.</param>
        /// <exception cref="IdTokenValidationException">Exception thrown if <paramref name="token"/> fails to
        /// meet the requirements specified by <paramref name="required"/>.
        /// </exception>
        private static void AssertTokenClaimsMeetRequirements(IdTokenRequirements required, JwtSecurityToken token, DateTime pointInTime)
        {
            var epochNow = EpochTime.GetIntDate(pointInTime);

            // Issuer
            if (string.IsNullOrWhiteSpace(token.Issuer))
                throw new IdTokenValidationException("Issuer (iss) claim must be a string present in the ID token.");
            if (token.Issuer != required.Issuer)
                throw new IdTokenValidationException($"Issuer (iss) claim mismatch in the ID token; expected \"{required.Issuer}\", found \"{token.Issuer}\".");

            // Subject
            if (string.IsNullOrWhiteSpace(token.Subject))
                throw new IdTokenValidationException("Subject (sub) claim must be a string present in the ID token.");

            // Audience
            var audienceCount = token.Audiences.Count();
            if (audienceCount == 0)
                throw new IdTokenValidationException("Audience (aud) claim must be a string or array of strings present in the ID token.");
            if (!token.Audiences.Contains(required.Audience))
                throw new IdTokenValidationException($"Audience (aud) claim mismatch in the ID token; expected \"{required.Audience}\" but was not one of \"{String.Join(", ", token.Audiences)}\".");

            {
                // Expires at
                var exp = GetEpoch(token.Claims, JwtRegisteredClaimNames.Exp);
                if (exp == null)
                    throw new IdTokenValidationException("Expiration Time (exp) claim must be an integer present in the ID token.");
                var expiration = exp + required.Leeway.TotalSeconds;
                if (epochNow >= expiration)
                    throw new IdTokenValidationException($"Expiration Time (exp) claim error in the ID token; current time ({epochNow}) is after expiration time ({exp}).");
            }

            // Issued at
            var iat = GetEpoch(token.Claims, JwtRegisteredClaimNames.Iat);
            if (iat == null)
                throw new IdTokenValidationException("Issued At (iat) claim must be an integer present in the ID token.");

            // Nonce
            if (required.Nonce != null)
            {
                if (string.IsNullOrWhiteSpace(token.Payload.Nonce))
                    throw new IdTokenValidationException("Nonce (nonce) claim must be a string present in the ID token.");
                if (token.Payload.Nonce != required.Nonce)
                    throw new IdTokenValidationException($"Nonce (nonce) claim mismatch in the ID token; expected \"{required.Nonce}\", found \"{token.Payload.Nonce}\".");
            }

            // Authorized Party
            if (audienceCount > 1)
            {
                if (string.IsNullOrWhiteSpace(token.Payload.Azp))
                    throw new IdTokenValidationException("Authorized Party (azp) claim must be a string present in the ID token when Audiences (aud) claim has multiple values.");
                if (token.Payload.Azp != required.Audience)
                    throw new IdTokenValidationException($"Authorized Party (azp) claim mismatch in the ID token; expected \"{required.Audience}\", found \"{token.Payload.Azp}\".");
            }

            // Authentication time
            if (required.MaxAge.HasValue)
            {
                var authTime = GetEpoch(token.Claims, JwtRegisteredClaimNames.AuthTime);
                if (!authTime.HasValue)
                    throw new IdTokenValidationException("Authentication Time (auth_time) claim must be an integer present in the ID token when MaxAge specified.");

                var authValidUntil = (long)(authTime + required.MaxAge.Value.TotalSeconds + required.Leeway.TotalSeconds);

                if (epochNow > authValidUntil)
                    throw new IdTokenValidationException($"Authentication Time (auth_time) claim in the ID token indicates that too much time has passed since the last end-user authentication. Current time ({epochNow}) is after last auth at {authValidUntil}.");
            }

            // Organization
            if (!string.IsNullOrWhiteSpace(required.Organization))
            {
                var organizationClaim = required.Organization.StartsWith("org_") ? Auth0ClaimNames.OrganizationId : Auth0ClaimNames.OrganizationName;
                var organizationClaimValue = GetClaimValue(token.Claims, organizationClaim);
                var expectedOrganization = organizationClaim == Auth0ClaimNames.OrganizationName ? required.Organization.ToLower() : required.Organization;

                if (string.IsNullOrWhiteSpace(organizationClaimValue))
                    throw new IdTokenValidationException($"Organization ({organizationClaim}) claim must be a string present in the ID token.");
                if (organizationClaimValue != expectedOrganization)
                    throw new IdTokenValidationException($"Organization ({organizationClaim}) claim mismatch in the ID token; expected \"{expectedOrganization}\", found \"{organizationClaimValue}\".");
            }
        }

        /// <summary>
        /// Get a epoch (Unix time) value for a given claim.
        /// </summary>
        /// <param name="claims"><see cref="IEnumerable{Claim}"/>Claims to search the <paramref name="claimType"/> for.</param>
        /// <param name="claimType">Type of claim to search the <paramref name="claims"/> for.  See <see cref="JwtRegisteredClaimNames"/> for possible names.</param>
        /// <returns>A <see cref="Nullable{Int64}"/> containing the epoch value or <see langword="null"/> if no matching value was found.</returns>
        private static long? GetEpoch(IEnumerable<Claim> claims, string claimType)
        {
            var claim = claims.FirstOrDefault(t => t.Type == claimType);
            if (claim == null) return null;

            return (long)Convert.ToDouble(claim.Value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Get the value for a given claim.
        /// </summary>
        /// <param name="claims"><see cref="IEnumerable{Claim}"/>Claims to search the <paramref name="claimType"/> for.</param>
        /// <param name="claimType">Type of claim to search the <paramref name="claims"/> for. See <see cref="JwtRegisteredClaimNames"/> or <see cref="Auth0ClaimNames"/> for possible names.</param>
        /// <returns><see cref="string"/> containing the value or <see langword="null"/> if no matching value was found.</returns>
        private static string GetClaimValue(IEnumerable<Claim> claims, string claimType)
        {
            var claim = claims.SingleOrDefault(t => t.Type == claimType);
            if (claim == null) return null;

            return claim.Value;
        }
    }
}
