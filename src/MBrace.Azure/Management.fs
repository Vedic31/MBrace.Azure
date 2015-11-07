﻿namespace MBrace.Azure

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography.X509Certificates
open System.Xml.Linq

open MBrace.Core.Internals
open MBrace.Runtime

open Microsoft.Azure
open Microsoft.WindowsAzure.Management
open Microsoft.WindowsAzure.Management.Compute
open Microsoft.WindowsAzure.Management.Compute.Models
open Microsoft.WindowsAzure.Management.Storage
open Microsoft.WindowsAzure.Management.Storage.Models
open Microsoft.WindowsAzure.Management.ServiceBus
open Microsoft.WindowsAzure.Management.ServiceBus.Models

/// Azure Region string identifier
type Region = string
/// Azure VM type string identifier
type VMSize = string

/// Collection of preset Azure Region identifiers
type Regions = 
    static member South_Central_US  : Region = "South Central US"
    static member West_US           : Region = "West US"
    static member Central_US        : Region = "Central US"
    static member East_US           : Region = "East US"
    static member East_US_2         : Region = "East US 2"
    static member North_Europe      : Region = "North Europe"
    static member West_Europe       : Region = "West Europe"
    static member Southeast_Asia    : Region = "Southeast Asia"
    static member East_Asia         : Region = "East Asia"

/// Collection of preset Azure VM type identifiers
type VMSizes = 
    static member A10               : VMSize = "A10"
    static member A11               : VMSize = "A11"
    static member A5                : VMSize = "A5"
    static member A6                : VMSize = "A6"
    static member A7                : VMSize = "A7"
    static member A8                : VMSize = "A8"
    static member A9                : VMSize = "A9"
    static member A4                : VMSize = "ExtraLarge"
    static member A0                : VMSize = "ExtraSmall"
    static member A3                : VMSize = "Large"
    static member A2                : VMSize = "Medium"
    static member A1                : VMSize = "Small"
    static member Extra_Large       : VMSize = "ExtraLarge"
    static member Large             : VMSize = "Large"
    static member Medium            : VMSize = "Medium"
    static member Small             : VMSize = "Small"
    static member Extra_Small       : VMSize = "ExtraSmall"
    static member Standard_D1       : VMSize = "Standard_D1"
    static member Standard_D11      : VMSize = "Standard_D11"
    static member Standard_D11_v2   : VMSize = "Standard_D11_v2"
    static member Standard_D12      : VMSize = "Standard_D12"
    static member Standard_D12_v2   : VMSize = "Standard_D12_v2"
    static member Standard_D13      : VMSize = "Standard_D13"
    static member Standard_D13_v2   : VMSize = "Standard_D13_v2"
    static member Standard_D14      : VMSize = "Standard_D14"
    static member Standard_D14_v2   : VMSize = "Standard_D14_v2"
    static member Standard_D1_v2    : VMSize = "Standard_D1_v2"
    static member Standard_D2       : VMSize = "Standard_D2"
    static member Standard_D2_v2    : VMSize = "Standard_D2_v2"
    static member Standard_D3       : VMSize = "Standard_D3"
    static member Standard_D3_v2    : VMSize = "Standard_D3_v2"
    static member Standard_D4       : VMSize = "Standard_D4"
    static member Standard_D4_v2    : VMSize = "Standard_D4_v2"
    static member Standard_D5_v2    : VMSize = "Standard_D5_v2"

/// Azure subscription record
[<AutoSerializable(false)>]
type Subscription = 
    { 
        /// Human-readable subscription name
        Name : string
        /// Subscription identifier
        Id : string  
        /// X509 management certificate
        ManagementCertificate : string
        /// Azure service management url
        ServiceManagementUrl : string
    }

/// Azure publish settings record
[<AutoSerializable(false)>]
type PublishSettings = 
    { 
        /// Set of Azure subscriptions
        Subscriptions: Subscription [] 
    }

    /// Parse publish settings found in given xml string
    static member Parse(xml : string) = 
        let parseSubscription (elem : XElement) = 
            let name = elem.Attribute(XName.op_Implicit "Name").Value
            let id = elem.Attribute(XName.op_Implicit "Id").Value
            let mc = elem.Attribute(XName.op_Implicit "ManagementCertificate").Value
            let smu = elem.Attribute(XName.op_Implicit "ServiceManagementUrl").Value
            {   Name = name;
                Id = id;
                ManagementCertificate = mc
                ServiceManagementUrl = smu }

        let doc = XDocument.Parse xml
        let pubData = doc.Element(XName.op_Implicit "PublishData")
        let pubProfile = pubData.Element(XName.op_Implicit "PublishProfile")
        let subs = [| for s in pubProfile.Elements(XName.op_Implicit "Subscription") -> parseSubscription s |]
        { Subscriptions = subs }

    /// Parse publish settings from given local file path
    static member ParseFile(publishSettingsFile : string) =
        PublishSettings.Parse(File.ReadAllText publishSettingsFile)

[<AutoOpen>]
module private Details = 

    let urlForPackage mbraceNugetVersionTag vmSize = 
        sprintf "https://github.com/mbraceproject/MBrace.Azure/releases/download/%s/MBrace.Azure.CloudService-%s.cspkg" mbraceNugetVersionTag vmSize
        //sprintf "https://github.com/mbraceproject/bits/raw/master/%s/MBrace.Azure.CloudService-%s.cspkg" mbraceVersion vmSize

    let resourcePrefix = "mbrace"
    let generateResourceName() = resourcePrefix + Guid.NewGuid().ToString("N").[..7]
    let defaultExtendedProperties = dict [ "IsMBraceAsset", "true"]
    let isMBraceAsset (extendedProperties:IDictionary<string, string>) = extendedProperties.ContainsKey "IsMBraceAsset"

    [<AutoSerializable(false)>]
    type AzureClient =
        { Subscription : string
          Credentials : CertificateCloudCredentials
          Storage : StorageManagementClient
          ServiceBus : ServiceBusManagementClient
          Compute : ComputeManagementClient
          Management : ManagementClient }
        static member OfCredentials (subscription:Subscription) credentials =
            { Subscription = subscription.Name
              Credentials = credentials
              Storage = new StorageManagementClient(credentials)
              ServiceBus = new ServiceBusManagementClient(credentials)
              Compute = new ComputeManagementClient(credentials)
              Management = new ManagementClient(credentials) }

    module Credentials =
        let asCredentials (subscription:Subscription) =
            let cert = new X509Certificate2(Convert.FromBase64String subscription.ManagementCertificate)
            new CertificateCloudCredentials(subscription.Id, cert)

        let connectToAzure subscriptionOpt pubSettingsFilePath =
            let pubSettings = PublishSettings.ParseFile pubSettingsFilePath
            let subscriptions = pubSettings.Subscriptions
            if subscriptions.Length = 0 then
                invalidArg "pubSettingsFilePath" <| sprintf "no Azure subscriptions found in pubsettings file %A" pubSettingsFilePath

            let subscription = defaultArg subscriptionOpt subscriptions.[0].Name

            match subscriptions |> Array.tryFind(fun s -> s.Name = subscription) with 
            | None -> invalidArg "subscriptionOpt" <| sprintf "couldn't find subscription %s in %s" subscription pubSettingsFilePath
            | Some sub -> sub |> asCredentials |> AzureClient.OfCredentials sub

    module Infrastructure =
        let listRegions (client:AzureClient) =
            let requiredServices = [ "Compute"; "Storage" ]
            let rolesForClient = set(client.Management.RoleSizes.List() |> Seq.map(fun r -> r.Name))
            client.Management.Locations.List()
            |> Seq.filter(fun location -> requiredServices |> List.forall(location.AvailableServices.Contains))
            |> Seq.map(fun location ->
                let rolesInLocation = set location.ComputeCapabilities.WebWorkerRoleSizes
                location.Name, rolesForClient |> Set.intersect rolesInLocation |> Seq.toList)
            |> Seq.toList


    module Storage =
        open Microsoft.WindowsAzure.Storage

        let listStorageAccounts region (client:AzureClient) =
            [ for account in client.Storage.StorageAccounts.List() do
                let hasLocationData, storageAccountLocation = account.ExtendedProperties.TryGetValue "ResourceLocation"
                if hasLocationData && storageAccountLocation = region then 
                   yield account ]

        let tryFindMBraceStorage region (client:AzureClient) =
            client
            |> listStorageAccounts region
            |> List.filter(fun account -> account.ExtendedProperties |> isMBraceAsset)
            |> List.map(fun account -> account.Name)
            |> function [] -> None | h::_ -> Some h

        let rec createMBraceStorageAccount (logger : ISystemLogger) (region : string) (client:AzureClient) = async {
            let accountName = generateResourceName()
            let! (availability : CheckNameAvailabilityResponse) = client.Storage.StorageAccounts.CheckNameAvailabilityAsync accountName 
            if not availability.IsAvailable then return! createMBraceStorageAccount logger region client
            else
                let result = client.Storage.StorageAccounts.Create(StorageAccountCreateParameters(Name = accountName, AccountType = "Standard_LRS", Location = region, ExtendedProperties = defaultExtendedProperties))
                if result.Status = OperationStatus.Failed then 
                    return invalidOp result.Error.Message
                else 
                    logger.Logf LogLevel.Info "Created new storage account %A" accountName
                    return accountName 
        }

        let getConnectionString accountName (client:AzureClient) =
            async { 
                let! (keys : StorageAccountGetKeysResponse) = client.Storage.StorageAccounts.GetKeysAsync accountName
                return sprintf """DefaultEndpointsProtocol=https;AccountName=%s;AccountKey=%s""" accountName keys.PrimaryKey
            }

        let getDeploymentContainer connectionString =
            let account = CloudStorageAccount.Parse connectionString
            let blobClient = account.CreateCloudBlobClient()
            blobClient.GetContainerReference "deployments"

        let configureMBraceStorageAccount connectionString = async {
            let container = getDeploymentContainer connectionString
            do! container.CreateIfNotExistsAsync()
        }

        let getDefaultMBraceStorageAccountName (logger : ISystemLogger) region client = async {
            match tryFindMBraceStorage region client with
            | Some storage -> 
                logger.Logf LogLevel.Info "Reusing existing storage account %s" storage
                return storage
            | None -> 
                return! createMBraceStorageAccount logger region client
        }

        let getMBraceStorageConnectionString accountName (client:AzureClient) = async {
            let! connectionString = getConnectionString accountName client 
            configureMBraceStorageAccount connectionString |> ignore
            return connectionString
        }

    module ServiceBus =
        let rec waitUntilState checkState ns (client:AzureClient) = async {
            let! result = client.ServiceBus.Namespaces.GetAsync ns |> Async.AwaitTaskCorrect |> Async.Catch
            let status = match result with Choice1Of2 ns -> ns.Namespace.Status | Choice2Of2 _ -> ""
            if not (checkState status) then
                do! Async.Sleep 2000
                return! waitUntilState checkState ns client
        }

        let rec createNamespace (logger : ISystemLogger) region (client:AzureClient) = async {
            let namespaceName = generateResourceName()
            logger.Logf LogLevel.Info "checking availability of service bus namespace %s" namespaceName
            let! (availability : CheckNamespaceAvailabilityResponse) = client.ServiceBus.Namespaces.CheckAvailabilityAsync namespaceName
            if not availability.IsAvailable then 
                return! createNamespace logger region client
            else
                logger.Logf LogLevel.Info "creating service bus namespace %s" namespaceName
                let result = client.ServiceBus.Namespaces.Create(namespaceName, region) 

                do! client |> waitUntilState ((=) "Active") namespaceName

                if result.StatusCode <> Net.HttpStatusCode.OK then 
                    return failwithf "Failed to create service bus: %O" result.StatusCode
                else 
                    logger.Logf LogLevel.Info "Created new default MBrace Service Bus namespace %s" namespaceName
                    return namespaceName 
        }

        let listNamespaces region (client:AzureClient) = async {
            let! (nss : ServiceBusNamespacesResponse) = client.ServiceBus.Namespaces.ListAsync()
            return nss |> Seq.filter (fun account -> account.Region = region) |> Seq.toList
        }

        let tryFindMBraceNamespace region (client:AzureClient) = async {
            let! accounts = listNamespaces region client
            return
                accounts
                |> Seq.filter(fun ns -> ns.Name.StartsWith resourcePrefix)
                |> Seq.map(fun ns -> ns.Name)
                |> Seq.tryPick Some
        }

        let getConnectionString namespaceName (client:AzureClient) = async {
            let rule = client.ServiceBus.Namespaces.ListAuthorizationRules namespaceName |> Seq.find(fun rule -> rule.KeyName = "RootManageSharedAccessKey")
            return sprintf """Endpoint=sb://%s.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=%s""" namespaceName rule.PrimaryKey
        } 

        let findOrCreateMBraceNamespace (logger : ISystemLogger) region client = async {
            let! nsOpt = tryFindMBraceNamespace region client
            match nsOpt with
            | Some ns -> 
                logger.Logf LogLevel.Info "Reusing existing Service Bus namespace %A" ns
                return ns
            | None -> return! createNamespace logger region client
        }

        let getDefaultMBraceNamespace (logger : ISystemLogger) region client = async {
            let! ns = findOrCreateMBraceNamespace logger region client
            let! connectionString = getConnectionString ns client
            return ns, connectionString
        }

        let deleteNamespace namespaceName (client:AzureClient) = async {
            let! _response = client.ServiceBus.Namespaces.DeleteAsync namespaceName
            do! client |> waitUntilState ((<>) "Removing") namespaceName
        }

    module Clusters =
        open System.IO

        type Node = { Size : string; Status : string }
        type ClusterDetails = {
            Name : string
            CreatedTime : DateTime
            ServiceStatus : string
            DeploymentStatus : string option
            Nodes : Node list }

        let validateClusterName (client:AzureClient) clusterName = async { 
            let result = client.Compute.HostedServices.CheckNameAvailability clusterName
            if not result.IsAvailable then return invalidOp result.Reason
        }

        let getConnectionStrings clusterName (client:AzureClient) = async {
            let! (service : HostedServiceGetDetailedResponse) = client.Compute.HostedServices.GetDetailedAsync clusterName
            let storageConnectionString = service.Properties.ExtendedProperties.["StorageAccountConnectionString"]
            let serviceBusConnectionString = service.Properties.ExtendedProperties.["ServiceBusConnectionString"]
            let serviceBusNamespace = service.Properties.ExtendedProperties.["ServiceBusName"]
            return storageConnectionString, serviceBusConnectionString, serviceBusNamespace
        }

        let listRunningMBraceClusters (client:AzureClient) = async {
            let! (services : HostedServiceListResponse) = client.Compute.HostedServices.ListAsync()
            let getHostedServiceInfo (service : HostedServiceListResponse.HostedService) =
                if service.Properties.ExtendedProperties |> isMBraceAsset then 
                    let deployment =
                        try client.Compute.Deployments.GetBySlot(service.ServiceName, DeploymentSlot.Production) |> Some
                        with _ -> None

                    let info = 
                        {   Name = service.ServiceName
                            CreatedTime = service.Properties.DateCreated
                            ServiceStatus = service.Properties.Status.ToString()
                            DeploymentStatus = deployment |> Option.map(fun deployment -> deployment.Status.ToString())
                            Nodes =
                                deployment
                                |> Option.map(fun deployment ->
                                    deployment.RoleInstances
                                    |> Seq.map(fun instance -> { Size = instance.InstanceSize; Status = instance.InstanceStatus })
                                    |> Seq.toList)
                                |> defaultArg <| [] 
                        } 

                    Some (info |> sprintf "%+A")
                else
                    None

            return services |> Seq.choose getHostedServiceInfo |> Seq.toList
        }

        let buildMBraceConfig serviceName instances storageConnection serviceBusConnection =
            sprintf """<?xml version="1.0" encoding="utf-8"?>
    <ServiceConfiguration serviceName="%s" xmlns="http://schemas.microsoft.com/ServiceHosting/2008/10/ServiceConfiguration" osFamily="4" osVersion="*" schemaVersion="2015-04.2.6">
      <Role name="MBrace.Azure.WorkerRole">
        <Instances count="%d" />
        <ConfigurationSettings>
          <Setting name="MBrace.StorageConnectionString" value="%s" />
          <Setting name="MBrace.ServiceBusConnectionString" value="%s" />
          <Setting name="Microsoft.WindowsAzure.Plugins.Diagnostics.ConnectionString" value="" />
        </ConfigurationSettings>
      </Role>
    </ServiceConfiguration>""" serviceName instances storageConnection serviceBusConnection

        let createMBraceCluster (logger : ISystemLogger, clusterName, clusterLabel, region, packagePath, config, storageAccountName, storageConnectionString, serviceBusNamespace, serviceBusConnectionString) (client:AzureClient) = async {
            do! validateClusterName client clusterName 

            let extendedProperties =
                [ "StorageAccountName", storageAccountName
                  "StorageAccountConnectionString", storageConnectionString
                  "ServiceBusName", serviceBusNamespace
                  "ServiceBusConnectionString", serviceBusConnectionString ]
                @ (defaultExtendedProperties.Keys |> Seq.map(fun key -> key, defaultExtendedProperties.[key]) |> Seq.toList)
                |> dict

            logger.Logf LogLevel.Info "creating cloud service %s" clusterName
            let! _ = client.Compute.HostedServices.CreateAsync(HostedServiceCreateParameters(Location = region, ServiceName = clusterName, ExtendedProperties = extendedProperties))

            let container = Storage.getDeploymentContainer storageConnectionString
            let packageBlob = packagePath |> Path.GetFileName |> container.GetBlockBlobReference
            let blobSizesDoNotMatch() =
                packageBlob.FetchAttributes()
                packageBlob.Properties.Length <> FileInfo(packagePath).Length

            if (not (packageBlob.Exists()) || blobSizesDoNotMatch()) then
                logger.Logf LogLevel.Info "uploading package %s" packagePath
                packageBlob.UploadFromFile(packagePath, FileMode.Open)
        
            logger.Logf LogLevel.Info "scheduling cluster creation:\n  cluster %s\n  package uri %s\n  config %s" clusterName (packageBlob.Uri.ToString()) config
            let! (createOp : AzureOperationResponse) =
                client.Compute.Deployments.BeginCreatingAsync(
                    clusterName,
                    DeploymentSlot.Production,
                    DeploymentCreateParameters(
                        Label = clusterLabel,
                        Name = clusterName,
                        PackageUri = packageBlob.Uri,
                        Configuration = config,
                        StartDeployment = Nullable true,
                        TreatWarningsAsError = Nullable true))

            if createOp.StatusCode <> Net.HttpStatusCode.Accepted then 
                return failwith "error: HTTP request for creation operation was not accepted"
          }

        let deleteMBraceCluster (logger : ISystemLogger) clusterName (client:AzureClient) = async {
            let service = client.Compute.HostedServices.GetDetailed clusterName
            if service.Properties.ExtendedProperties |> isMBraceAsset then
                logger.Logf LogLevel.Info "deleting cluster %s" clusterName
                let deleteOp = client.Compute.Deployments.DeleteByName(clusterName, clusterName, true)
                if deleteOp.Status <> OperationStatus.Succeeded then return failwith deleteOp.Error.Message
                let deleteOp = client.Compute.HostedServices.Delete clusterName
                if deleteOp.StatusCode <> Net.HttpStatusCode.OK then return failwith (deleteOp.StatusCode.ToString()) 
            else
                logger.Logf LogLevel.Info "No MBrace cluster called %s found" clusterName
        }

type Management() = 
    static let defaultMBraceVersion = System.AssemblyVersionInformation.ReleaseTag

    static member CreateCluster(pubSettingsFile, region : Region, ?logger : ISystemLogger, ?ClusterName, ?Subscription, ?MBraceVersion, ?VMCount, ?StorageAccount, ?VMSize : VMSize, ?CloudServicePackage, ?ClusterLabel) = async { 
        let logger = match logger with Some l -> l | None -> new NullLogger() :> _
        let vmSize = defaultArg VMSize VMSizes.Large
        logger.Logf LogLevel.Info "using vm size %s" vmSize
        let! packagePath, infoString = async { 
            match CloudServicePackage with 
            | Some uriOrPath when System.Uri(uriOrPath).IsFile ->
                let path = System.Uri(uriOrPath).LocalPath
                logger.Logf LogLevel.Info "using cloud service package from %s" path
                return path, "custom"
            | _ -> 
                let mbraceVersion = defaultArg MBraceVersion defaultMBraceVersion
                logger.Logf LogLevel.Info "using MBrace version tag %s" mbraceVersion
                let uri = defaultArg CloudServicePackage (urlForPackage mbraceVersion vmSize)
                use wc = new System.Net.WebClient() 
                let tmp = System.IO.Path.GetTempFileName() 
                logger.Logf LogLevel.Info "downloading cloud service package from %s" uri
                wc.DownloadFile(uri, tmp)
                return tmp, mbraceVersion
        }

        let client = Credentials.connectToAzure Subscription pubSettingsFile 
        let clusterName = defaultArg ClusterName (generateResourceName())
        logger.Logf LogLevel.Info "using cluster name %s" clusterName
        let! storageAccountName = async {
            match StorageAccount with 
            | None -> return! Storage.getDefaultMBraceStorageAccountName logger region client
            | Some s -> return s
        }

        let! storageConnectionString = Storage.getMBraceStorageConnectionString storageAccountName client
        logger.Logf LogLevel.Info "using storage account name %s" storageAccountName
        logger.Logf LogLevel.Info "using storage connection string %s..." storageConnectionString.[0..20]
        let! serviceBusNamespace, serviceBusConnectionString = ServiceBus.getDefaultMBraceNamespace logger region client
        logger.Logf LogLevel.Info "using service bus namespace %s" serviceBusNamespace
        logger.Logf LogLevel.Info "using service bus connection string %s..." serviceBusConnectionString.[0..20]

        let numInstances = defaultArg VMCount 2
        let config = Clusters.buildMBraceConfig clusterName numInstances storageConnectionString serviceBusConnectionString

        let clusterLabel = defaultArg ClusterLabel (sprintf "MBrace cluster %s, package %s"  clusterName infoString)
        do! Clusters.createMBraceCluster(logger, clusterName, clusterLabel, region, packagePath, config, storageAccountName, storageConnectionString, serviceBusNamespace, serviceBusConnectionString) client

        let config = Configuration(storageConnectionString, serviceBusConnectionString)

        return config
    }

    static member DeleteCluster(pubSettingsFile, clusterName, ?Subscription, ?logger : ISystemLogger) = async { 
        let logger = match logger with Some l -> l | None -> new NullLogger() :> _
        let client = Credentials.connectToAzure Subscription pubSettingsFile 
        return! Clusters.deleteMBraceCluster logger clusterName client
    }

    static member GetClusters(pubSettingsFile, ?Subscription) = async { 
        let client = Credentials.connectToAzure Subscription pubSettingsFile
        return! Clusters.listRunningMBraceClusters client
    }