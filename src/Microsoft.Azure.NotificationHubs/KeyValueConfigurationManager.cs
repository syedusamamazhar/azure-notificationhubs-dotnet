﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved. 
// Licensed under the MIT License. See License.txt in the project root for 
// license information.
//------------------------------------------------------------

namespace Microsoft.Azure.NotificationHubs
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Configuration;
    using System.Globalization;
    using System.Security;
    using System.Text.RegularExpressions;
    using Microsoft.Azure.NotificationHubs.Auth;

    internal class KeyValueConfigurationManager
    {
        public const string ServiceBusConnectionKeyName = @"Microsoft.Azure.NotificationHubs.ConnectionString";
        public const string OperationTimeoutConfigName = @"OperationTimeout";
        public const string EntityPathConfigName = @"EntityPath";
        public const string EndpointConfigName = @"Endpoint";
        public const string SharedSecretIssuerConfigName = @"SharedSecretIssuer";
        public const string SharedSecretValueConfigName = @"SharedSecretValue";
        public const string SharedAccessKeyName = @"SharedAccessKeyName";
        public const string SharedAccessValueName = @"SharedAccessKey";
        public const string RuntimePortConfigName = @"RuntimePort";
        public const string ManagementPortConfigName = @"ManagementPort";
        public const string StsEndpointConfigName = @"StsEndpoint";
        public const string WindowsDomainConfigName = @"WindowsDomain";
        public const string WindowsUsernameConfigName = @"WindowsUsername";
        public const string WindowsPasswordConfigName = @"WindowsPassword";
        public const string OAuthDomainConfigName = @"OAuthDomain";
        public const string OAuthUsernameConfigName = @"OAuthUsername";
        public const string OAuthPasswordConfigName = @"OAuthPassword";

        internal const string ValueSeparator = @",";
        internal const string KeyValueSeparator = @"=";
        internal const string KeyDelimiter = @";";
        const string KeyAttributeEnumRegexString = @"(" +
                                                   EndpointConfigName + @"|" +
                                                   SharedAccessKeyName + @"|" +
                                                   EntityPathConfigName + @"|" +
                                                   SharedAccessValueName + @")";
        const string KeyDelimiterRegexString = KeyDelimiter + KeyAttributeEnumRegexString + KeyValueSeparator;

        // This is not designed to catch any custom parsing logic that SB has. We rely on SB to do the 
        // actual string validation. Also note the following characteristics:
        // 1. We are whitelisting known setting names intentionally here for security.
        // 2. The regex here excludes spaces.
        private static readonly Regex KeyRegex = new Regex(KeyAttributeEnumRegexString, RegexOptions.IgnoreCase);

        private static readonly Regex ValueRegex = new Regex(@"([^\s]+)");

        internal NameValueCollection connectionProperties;
        internal string connectionString;

        public KeyValueConfigurationManager(string connectionString)
        {
            this.Initialize(connectionString);
        }

        private void Initialize(string connection)
        {
            this.connectionString = connection;
            this.connectionProperties = CreateNameValueCollectionFromConnectionString(this.connectionString);
        }

        public string this[string key] => this.connectionProperties[key];

        private static NameValueCollection CreateNameValueCollectionFromConnectionString(string connectionString)
        {
            var settings = new NameValueCollection();
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                var connection = KeyValueConfigurationManager.KeyDelimiter + connectionString;
                var keyValues = Regex.Split(connection, KeyValueConfigurationManager.KeyDelimiterRegexString, RegexOptions.IgnoreCase);
                if (keyValues.Length > 0)
                {
                    // Regex.Split returns the array that include part of the delimiters, so it will look 
                    // something like this:
                    // { "", "Endpoint", "sb://a.b.c", "OperationTimeout", "01:20:30", ...}
                    // We should always get empty string for first element (except if we found no match at all).
                    if (!string.IsNullOrWhiteSpace(keyValues[0]))
                    {
                        throw new ConfigurationException(SRClient.AppSettingsConfigSettingInvalidKey(connectionString));
                    }

                    if (keyValues.Length % 2 != 1)
                    {
                        throw new ConfigurationException(SRClient.AppSettingsConfigSettingInvalidKey(connectionString));
                    }

                    for (var i = 1; i < keyValues.Length; i++)
                    {
                        var key = keyValues[i];
                        if (string.IsNullOrWhiteSpace(key) || !KeyRegex.IsMatch(key))
                        {
                            throw new ConfigurationException(SRClient.AppSettingsConfigSettingInvalidKey(key));
                        }

                        var value = keyValues[i + 1];
                        if (string.IsNullOrWhiteSpace(value) || !ValueRegex.IsMatch(value))
                        {
                            throw new ConfigurationException(SRClient.AppSettingsConfigSettingInvalidValue(key, value));
                        }

                        if (settings[key] != null)
                        {
                            throw new ConfigurationException(SRClient.AppSettingsConfigDuplicateSetting(key));
                        }

                        settings[key] = value;
                        i++;
                    }
                }
            }

            return settings;
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(this.connectionProperties[EndpointConfigName]))
            {
                throw new ConfigurationException(SRClient.AppSettingsConfigMissingSetting(EndpointConfigName, ServiceBusConnectionKeyName));
            }
        }

        public NamespaceManager CreateNamespaceManager()
        {
            this.Validate();

            string operationTimeout = this.connectionProperties[OperationTimeoutConfigName];
            IEnumerable<Uri> endpoints = GetEndpointAddresses(this.connectionProperties[EndpointConfigName], this.connectionProperties[ManagementPortConfigName]);
            IEnumerable<Uri> stsEndpoints = GetEndpointAddresses(this.connectionProperties[StsEndpointConfigName], null);
            string issuerName = this.connectionProperties[SharedSecretIssuerConfigName];
            string issuerKey = this.connectionProperties[SharedSecretValueConfigName];
            string sasKeyName = this.connectionProperties[SharedAccessKeyName];
            string sasKey = this.connectionProperties[SharedAccessValueName];
            string windowsDomain = this.connectionProperties[WindowsDomainConfigName];
            string windowsUsername = this.connectionProperties[WindowsUsernameConfigName];
            SecureString windowsPassword = this.GetWindowsPassword();
            string oauthDomain = this.connectionProperties[OAuthDomainConfigName];
            string oauthUsername = this.connectionProperties[OAuthUsernameConfigName];
            SecureString oauthPassword = this.GetOAuthPassword();

            try
            {
                TokenProvider provider = CreateTokenProvider(stsEndpoints, issuerName, issuerKey, sasKeyName, sasKey, windowsDomain, windowsUsername, windowsPassword, oauthDomain, oauthUsername, oauthPassword);
                if (string.IsNullOrEmpty(operationTimeout))
                {
                    return new NamespaceManager(endpoints, provider);
                }

                return new NamespaceManager(
                    endpoints,
                    new NamespaceManagerSettings()
                    {
                        TokenProvider = provider
                    });
            }
            catch (ArgumentException e)
            {
                throw new ConfigurationErrorsException(
                    SRClient.AppSettingsCreateManagerWithInvalidConnectionString(e.Message),
                    e);
            }
            catch (UriFormatException e)
            {
                throw new ConfigurationErrorsException(
                    SRClient.AppSettingsCreateManagerWithInvalidConnectionString(e.Message),
                    e);
            }
        }

        public SecureString GetWindowsPassword()
        {
            return GetSecurePassword(WindowsPasswordConfigName);
        }

        public SecureString GetOAuthPassword()
        {
            return GetSecurePassword(OAuthPasswordConfigName);
        }

        private IList<Uri> GetEndpointAddresses()
        {
            return GetEndpointAddresses(this.connectionProperties[EndpointConfigName], this.connectionProperties[RuntimePortConfigName]);
        }

        public static IList<Uri> GetEndpointAddresses(string uriEndpoints, string portString)
        {
            List<Uri> addresses = new List<Uri>();
            if (string.IsNullOrWhiteSpace(uriEndpoints))
            {
                return addresses;
            }

            string[] endpoints = uriEndpoints.Split(new string[] { ValueSeparator }, StringSplitOptions.RemoveEmptyEntries);
            if (endpoints == null || endpoints.Length == 0)
            {
                return addresses;
            }

            int port;
            if (!int.TryParse(portString, out port))
            {
                port = -1;
            }

            foreach (string endpoint in endpoints)
            {
                var address = new UriBuilder(endpoint);
                if (port > 0)
                {
                    address.Port = port;
                }

                addresses.Add(address.Uri);
            }

            return addresses;
        }

        SecureString GetSecurePassword(string configName)
        {
            SecureString password = null;
            string passwordString = this.connectionProperties[configName];
            if (!string.IsNullOrWhiteSpace(passwordString))
            {
                unsafe
                {
                    char[] array = passwordString.ToCharArray();
                    fixed (char* pChars = array)
                    {
                        password = new SecureString(pChars, array.Length);
                    }
                }
            }

            return password;
        }
    }
}
