﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Diagnostics
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using Microsoft.Azure.Documents;

    internal sealed class StoreResponseStatistics : CosmosDiagnosticsInternal
    {
        public readonly DateTime RequestResponseTime;
        public readonly StoreResult StoreResult;
        public readonly ResourceType RequestResourceType;
        public readonly OperationType RequestOperationType;
        public readonly Uri LocationEndpoint;
        public readonly bool IsSupplementalResponse;

        public StoreResponseStatistics(
            DateTime requestResponseTime,
            StoreResult storeResult,
            ResourceType resourceType,
            OperationType operationType,
            Uri locationEndpoint)
        {
            this.RequestResponseTime = requestResponseTime;
            this.StoreResult = storeResult;
            this.RequestResourceType = resourceType;
            this.RequestOperationType = operationType;
            this.LocationEndpoint = locationEndpoint;
            this.IsSupplementalResponse = operationType == OperationType.Head || operationType == OperationType.HeadFeed;
        }

        public override void Accept(CosmosDiagnosticsInternalVisitor visitor)
        {
            visitor.Visit(this);
        }

        public override TResult Accept<TResult>(CosmosDiagnosticsInternalVisitor<TResult> visitor)
        {
            return visitor.Visit(this);
        }
    }
}
