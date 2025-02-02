// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

// <auto-generated/>

#nullable disable

using Azure.ResourceManager.Management;

namespace Azure.ResourceManager.Resources
{
    /// <summary> A class to add extension methods to ManagementGroup. </summary>
    public static partial class ManagementGroupExtensions
    {
        #region Deployment
        /// <summary> Gets an object representing a DeploymentContainer along with the instance operations that can be performed on it. </summary>
        /// <param name="managementGroup"> The <see cref="ManagementGroup" /> instance the method will execute against. </param>
        /// <returns> Returns a <see cref="DeploymentContainer" /> object. </returns>
        public static DeploymentContainer GetDeployments(this ManagementGroup managementGroup)
        {
            return new DeploymentContainer(managementGroup);
        }
        #endregion
    }
}
