﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.SqlServer.Features.Schema;
using Xunit;

namespace Microsoft.Health.Fhir.Tests.Integration.Persistence
{
    [FhirStorageTestsFixtureArgumentSets(DataStore.SqlServer)]
    public class SqlServerSchemaUpgradeTests : IClassFixture<FhirStorageTestsFixture>
    {
        private readonly ISqlServerFhirStorageTestHelper _testHelper;
        private readonly SchemaUpgradeRunner _schemaRunner;

        public SqlServerSchemaUpgradeTests(FhirStorageTestsFixture fixture)
        {
            _testHelper = (SqlServerFhirStorageTestHelper)fixture.TestHelper;
            _schemaRunner = fixture.SchemaUpgradeRunner;
        }

        [Fact]
        public async Task GivenTwoSchemaInitializationMethods_WhenCreatingTwoDatabases_BothSchemasShouldBeEquivalent()
        {
            var snapshotDatabaseName = $"SNAPSHOT_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{BigInteger.Abs(new BigInteger(Guid.NewGuid().ToByteArray()))}";
            var diffDatabaseName = $"DIFF_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{BigInteger.Abs(new BigInteger(Guid.NewGuid().ToByteArray()))}";

            try
            {
                // Create two databases, one where we apply the the maximum supported version's snapshot SQL schema file
                await _testHelper.CreateAndInitializeDatabase(snapshotDatabaseName, SchemaVersionConstants.Max, forceIncrementalSchemaUpgrade: false);

                // And one where we apply .diff.sql files to upgrade the schema version to the maximum supported version.
                await _testHelper.CreateAndInitializeDatabase(diffDatabaseName, SchemaVersionConstants.Max, forceIncrementalSchemaUpgrade: true);

                bool isEqual = _testHelper.CompareDatabaseSchemas(snapshotDatabaseName, diffDatabaseName);
                Assert.True(isEqual);
            }
            finally
            {
                await _testHelper.DeleteDatabase(snapshotDatabaseName);
                await _testHelper.DeleteDatabase(diffDatabaseName);
            }
        }

        [Fact]
        public async Task GivenASchemaVersion_WhenApplyingDiffTwice_ShouldSucceed()
        {
            var snapshotDatabaseName = $"SNAPSHOT_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{BigInteger.Abs(new BigInteger(Guid.NewGuid().ToByteArray()))}";

            await _testHelper.CreateAndInitializeDatabase(snapshotDatabaseName, SchemaVersionConstants.Max - 1, forceIncrementalSchemaUpgrade: false);

            await _schemaRunner.ApplySchemaAsync(SchemaVersionConstants.Max, applyFullSchemaSnapshot: false, CancellationToken.None);

            await _schemaRunner.ApplySchemaAsync(SchemaVersionConstants.Max, applyFullSchemaSnapshot: false, CancellationToken.None);
        }
    }
}
