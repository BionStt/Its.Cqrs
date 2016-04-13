// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Its.Domain;
using Test.Domain.Ordering;
using Test.Domain.Ordering;

namespace Test.Domain.Ordering
{
    public class ConfirmPayment : Command<Order>
    {
        public PaymentId PaymentId { get; set; }
    }
}
