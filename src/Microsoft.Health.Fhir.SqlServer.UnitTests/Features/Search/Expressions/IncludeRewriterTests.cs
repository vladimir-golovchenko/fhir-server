// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.Health.Fhir.Core;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions.Visitors;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions.Visitors.QueryGenerators;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search.Expressions
{
    public class IncludeRewriterTests
    {
        private readonly SearchParameterDefinitionManager _searchParameterDefinitionManager;
        private IReadOnlyList<string> _includeTargetTypes = new List<string>() { "MedicationRequest" };

        public IncludeRewriterTests()
        {
            ModelInfoProvider.SetProvider(new VersionSpecificModelInfoProvider());
            _searchParameterDefinitionManager = new SearchParameterDefinitionManager(ModelInfoProvider.Instance);
            _searchParameterDefinitionManager.Start();
        }

        [Fact]
        public void GivenASqlRootExpressionWithIncludes_WhenVisitedByIncludeRewriter_OrderIterateExpressionsAfterOtherSearchParametersAndAfterIncludeExpressionsTheyAreIteratingOver()
        {
            // Order the following query:
            // [base]/MedicationDispense?_include:iterate=Patient:general-practitioner&_include:iterate=MedicationRequest:patient&_include=MedicationDispense:prescription&_id=smart-MedicationDispense-567

            var refSearchParameter = _searchParameterDefinitionManager.GetSearchParameter("Patient", "general-practitioner");
            var includeIteratePatientGeneralPractitioner = new IncludeExpression("Patient", refSearchParameter, "Patient", null, null, false, false, true);

            refSearchParameter = _searchParameterDefinitionManager.GetSearchParameter("MedicationRequest", "patient");
            var includeIterateMedicationRequestPatient = new IncludeExpression("MedicationRequest", refSearchParameter, "MedicationRequest", null, null, false, false, true);

            refSearchParameter = _searchParameterDefinitionManager.GetSearchParameter("MedicationDispense", "prescription");
            var includeMedicationDispensePrescription = new IncludeExpression("MedicationDispense", refSearchParameter, "MedicationDispense", null, null, false, false, false);

            Expression denormalizedExpression = Expression.And(new List<Expression>
                {
                    new SearchParameterExpression(new SearchParameterInfo("_type"), new StringExpression(StringOperator.Equals, FieldName.String, null, "MedicationDispense", false)),
                    new SearchParameterExpression(new SearchParameterInfo("_id"), new StringExpression(StringOperator.Equals, FieldName.String, null, "smart-MedicationDispense-567", false)),
                });

            var sqlExpression = new SqlRootExpression(
                new List<TableExpression>
                {
                    new TableExpression(null, null, denormalizedExpression, TableExpressionKind.All),
                    new TableExpression(IncludeQueryGenerator.Instance,  includeIteratePatientGeneralPractitioner, null, TableExpressionKind.Include),
                    new TableExpression(IncludeQueryGenerator.Instance,  includeIterateMedicationRequestPatient, null, TableExpressionKind.Include),
                    new TableExpression(IncludeQueryGenerator.Instance,  includeMedicationDispensePrescription, null, TableExpressionKind.Include),
                    new TableExpression(null, null, null, TableExpressionKind.Top),
                },
                new List<Expression>());

            var reorderedExpressions = ((SqlRootExpression)sqlExpression.AcceptVisitor(IncludeRewriter.Instance)).TableExpressions;

            // Assert the number of expressions and their order is correct, including IncludeUnionAll expression, which was added in the IncludeRewriter visit.
            Assert.NotNull(reorderedExpressions);
            Assert.Equal(9, reorderedExpressions.Count);

            Assert.Equal(TableExpressionKind.All, reorderedExpressions[0].Kind);
            Assert.Equal(TableExpressionKind.Top, reorderedExpressions[1].Kind);

            Assert.Equal(TableExpressionKind.Include, reorderedExpressions[2].Kind);
            var includeExpression = (IncludeExpression)reorderedExpressions[2].NormalizedPredicate;
            Assert.Equal("MedicationDispense", includeExpression.ResourceType);
            Assert.Equal("prescription", includeExpression.ReferenceSearchParameter.Name);

            Assert.Equal(TableExpressionKind.IncludeLimit, reorderedExpressions[3].Kind);

            Assert.Equal(TableExpressionKind.Include, reorderedExpressions[4].Kind);
            includeExpression = (IncludeExpression)reorderedExpressions[4].NormalizedPredicate;
            Assert.Equal("MedicationRequest", includeExpression.ResourceType);
            Assert.Equal("patient", includeExpression.ReferenceSearchParameter.Name);

            Assert.Equal(TableExpressionKind.IncludeLimit, reorderedExpressions[5].Kind);

            Assert.Equal(TableExpressionKind.Include, reorderedExpressions[6].Kind);
            includeExpression = (IncludeExpression)reorderedExpressions[6].NormalizedPredicate;
            Assert.Equal("Patient", includeExpression.ResourceType);
            Assert.Equal("general-practitioner", includeExpression.ReferenceSearchParameter.Name);

            Assert.Equal(TableExpressionKind.IncludeLimit, reorderedExpressions[7].Kind);

            Assert.Equal(TableExpressionKind.IncludeUnionAll, reorderedExpressions[8].Kind);
        }

        [Fact]
        public void GivenASqlRootExpressionWithRevIncludes_WhenVisitedByIncludeRewriter_OrderIterateExpressionsAfterOtherSearchParametersAndAfterIncludeExpressionsTheyAreIteratingOver()
        {
            // Order the following query:
            // [base]/Organization?_revinclude:iterate=MedicationDispense:prescription&_revinclude:iterate=MedicationRequest:patient&_revinclude=Patient:organization&_id=organization-id

            var refSearchParameter = _searchParameterDefinitionManager.GetSearchParameter("MedicationDispense", "prescription");
            var includeIteratePatientGeneralPractitioner = new IncludeExpression("MedicationDispense", refSearchParameter, "MedicationDispense", null, null, false, true, true);

            refSearchParameter = _searchParameterDefinitionManager.GetSearchParameter("MedicationRequest", "patient");
            var includeIterateMedicationRequestPatient = new IncludeExpression("MedicationRequest", refSearchParameter, "MedicationRequest", null, null, false, true, true);

            refSearchParameter = _searchParameterDefinitionManager.GetSearchParameter("Patient", "organization");
            var includeMedicationDispensePrescription = new IncludeExpression("Patient", refSearchParameter, "Patient", null, null, false, true, false);

            Expression denormalizedExpression = Expression.And(new List<Expression>
                {
                    new SearchParameterExpression(new SearchParameterInfo("_type"), new StringExpression(StringOperator.Equals, FieldName.String, null, "Organization", false)),
                    new SearchParameterExpression(new SearchParameterInfo("_id"), new StringExpression(StringOperator.Equals, FieldName.String, null, "organization-id", false)),
                });

            var sqlExpression = new SqlRootExpression(
                new List<TableExpression>
                {
                    new TableExpression(null, null, denormalizedExpression, TableExpressionKind.All),
                    new TableExpression(IncludeQueryGenerator.Instance,  includeIteratePatientGeneralPractitioner, null, TableExpressionKind.Include),
                    new TableExpression(IncludeQueryGenerator.Instance,  includeIterateMedicationRequestPatient, null, TableExpressionKind.Include),
                    new TableExpression(IncludeQueryGenerator.Instance,  includeMedicationDispensePrescription, null, TableExpressionKind.Include),
                    new TableExpression(null, null, null, TableExpressionKind.Top),
                },
                new List<Expression>());

            var reorderedExpressions = ((SqlRootExpression)sqlExpression.AcceptVisitor(IncludeRewriter.Instance)).TableExpressions;

            // Assert the number of expressions and their order is correct, including IncludeUnionAll expression, which was added in the IncludeRewriter visit.
            Assert.NotNull(reorderedExpressions);
            Assert.Equal(9, reorderedExpressions.Count);

            Assert.Equal(TableExpressionKind.All, reorderedExpressions[0].Kind);
            Assert.Equal(TableExpressionKind.Top, reorderedExpressions[1].Kind);

            Assert.Equal(TableExpressionKind.Include, reorderedExpressions[2].Kind);
            var includeExpression = (IncludeExpression)reorderedExpressions[2].NormalizedPredicate;
            Assert.Equal("Patient", includeExpression.ResourceType);
            Assert.Equal("organization", includeExpression.ReferenceSearchParameter.Name);

            Assert.Equal(TableExpressionKind.IncludeLimit, reorderedExpressions[3].Kind);

            Assert.Equal(TableExpressionKind.Include, reorderedExpressions[4].Kind);
            includeExpression = (IncludeExpression)reorderedExpressions[4].NormalizedPredicate;
            Assert.Equal("MedicationRequest", includeExpression.ResourceType);
            Assert.Equal("patient", includeExpression.ReferenceSearchParameter.Name);

            Assert.Equal(TableExpressionKind.IncludeLimit, reorderedExpressions[5].Kind);

            Assert.Equal(TableExpressionKind.Include, reorderedExpressions[6].Kind);
            includeExpression = (IncludeExpression)reorderedExpressions[6].NormalizedPredicate;
            Assert.Equal("MedicationDispense", includeExpression.ResourceType);
            Assert.Equal("prescription", includeExpression.ReferenceSearchParameter.Name);

            Assert.Equal(TableExpressionKind.IncludeLimit, reorderedExpressions[7].Kind);

            Assert.Equal(TableExpressionKind.IncludeUnionAll, reorderedExpressions[8].Kind);
        }

        [Fact]
        public void GivenASqlRootExpressionWithIncludesAndRevIncludes_WhenVisitedByIncludeRewriter_OrderIterateExpressionsAfterOtherSearchParametersAndAfterIncludeExpressionsTheyAreIteratingOver()
        {
            // Order the following query:
            // [base]/Organization?_include:iterate=MedicationDispense:prescription&_revinclude:iterate=MedicationDispense:patient&_revinclude=Patient:organization&_id=organization-id

            var refSearchParameter = _searchParameterDefinitionManager.GetSearchParameter("MedicationDispense", "prescription");
            var includeIteratePatientGeneralPractitioner = new IncludeExpression("MedicationDispense", refSearchParameter, "MedicationDispense", null, null, false, false, true);

            refSearchParameter = _searchParameterDefinitionManager.GetSearchParameter("MedicationDispense", "patient");
            var includeIterateMedicationRequestPatient = new IncludeExpression("MedicationDispense", refSearchParameter, "MedicationDispense", null, null, false, true, true);

            refSearchParameter = _searchParameterDefinitionManager.GetSearchParameter("Patient", "organization");
            var includeMedicationDispensePrescription = new IncludeExpression("Patient", refSearchParameter, "Patient", null, null, false, true, false);

            Expression denormalizedExpression = Expression.And(new List<Expression>
                {
                    new SearchParameterExpression(new SearchParameterInfo("_type"), new StringExpression(StringOperator.Equals, FieldName.String, null, "Organization", false)),
                    new SearchParameterExpression(new SearchParameterInfo("_id"), new StringExpression(StringOperator.Equals, FieldName.String, null, "organization-id", false)),
                });

            var sqlExpression = new SqlRootExpression(
                new List<TableExpression>
                {
                    new TableExpression(null, null, denormalizedExpression, TableExpressionKind.All),
                    new TableExpression(IncludeQueryGenerator.Instance,  includeIteratePatientGeneralPractitioner, null, TableExpressionKind.Include),
                    new TableExpression(IncludeQueryGenerator.Instance,  includeIterateMedicationRequestPatient, null, TableExpressionKind.Include),
                    new TableExpression(IncludeQueryGenerator.Instance,  includeMedicationDispensePrescription, null, TableExpressionKind.Include),
                    new TableExpression(null, null, null, TableExpressionKind.Top),
                },
                new List<Expression>());

            var reorderedExpressions = ((SqlRootExpression)sqlExpression.AcceptVisitor(IncludeRewriter.Instance)).TableExpressions;

            // Assert the number of expressions and their order is correct, including IncludeUnionAll expression, which was added in the IncludeRewriter visit.
            Assert.NotNull(reorderedExpressions);
            Assert.Equal(9, reorderedExpressions.Count);

            Assert.Equal(TableExpressionKind.All, reorderedExpressions[0].Kind);
            Assert.Equal(TableExpressionKind.Top, reorderedExpressions[1].Kind);

            Assert.Equal(TableExpressionKind.Include, reorderedExpressions[2].Kind);
            var includeExpression = (IncludeExpression)reorderedExpressions[2].NormalizedPredicate;
            Assert.Equal("Patient", includeExpression.ResourceType);
            Assert.Equal("organization", includeExpression.ReferenceSearchParameter.Name);

            Assert.Equal(TableExpressionKind.IncludeLimit, reorderedExpressions[3].Kind);

            Assert.Equal(TableExpressionKind.Include, reorderedExpressions[4].Kind);
            includeExpression = (IncludeExpression)reorderedExpressions[4].NormalizedPredicate;
            Assert.Equal("MedicationDispense", includeExpression.ResourceType);
            Assert.Equal("patient", includeExpression.ReferenceSearchParameter.Name);

            Assert.Equal(TableExpressionKind.IncludeLimit, reorderedExpressions[5].Kind);

            Assert.Equal(TableExpressionKind.Include, reorderedExpressions[6].Kind);
            includeExpression = (IncludeExpression)reorderedExpressions[6].NormalizedPredicate;
            Assert.Equal("MedicationDispense", includeExpression.ResourceType);
            Assert.Equal("prescription", includeExpression.ReferenceSearchParameter.Name);

            Assert.Equal(TableExpressionKind.IncludeLimit, reorderedExpressions[7].Kind);

            Assert.Equal(TableExpressionKind.IncludeUnionAll, reorderedExpressions[8].Kind);
        }
    }
}