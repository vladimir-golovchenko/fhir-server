﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EnsureThat;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Models;
using Expression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    public class SearchOptionsFactory : ISearchOptionsFactory
    {
        private static readonly string SupportedTotalTypes = $"'{TotalType.Accurate}', '{TotalType.None}'".ToLower(CultureInfo.CurrentCulture);

        private static readonly List<string> IncludeIterateModifiers = new List<string> { "_include:iterate", "_include:recurse" };
        private static readonly List<string> RevIncludeIterateModifiers = new List<string> { "_revinclude:iterate", "_revinclude:recurse" };

        private readonly IExpressionParser _expressionParser;
        private readonly ISearchParameterDefinitionManager _searchParameterDefinitionManager;
        private readonly ILogger _logger;
        private readonly SearchParameterInfo _resourceTypeSearchParameter;
        private CoreFeatureConfiguration _featureConfiguration;

        public SearchOptionsFactory(
            IExpressionParser expressionParser,
            ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver searchParameterDefinitionManagerResolver,
            IOptions<CoreFeatureConfiguration> featureConfiguration,
            ILogger<SearchOptionsFactory> logger)
        {
            EnsureArg.IsNotNull(expressionParser, nameof(expressionParser));
            EnsureArg.IsNotNull(searchParameterDefinitionManagerResolver, nameof(searchParameterDefinitionManagerResolver));
            EnsureArg.IsNotNull(featureConfiguration?.Value, nameof(featureConfiguration));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _expressionParser = expressionParser;
            _searchParameterDefinitionManager = searchParameterDefinitionManagerResolver();
            _logger = logger;
            _featureConfiguration = featureConfiguration.Value;

            _resourceTypeSearchParameter = _searchParameterDefinitionManager.GetSearchParameter(ResourceType.Resource.ToString(), SearchParameterNames.ResourceType);
        }

        public SearchOptions Create(string resourceType, IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            return Create(null, null, resourceType, queryParameters);
        }

        public SearchOptions Create(string compartmentType, string compartmentId, string resourceType, IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            var searchOptions = new SearchOptions();

            string continuationToken = null;

            var searchParams = new SearchParams();
            var unsupportedSearchParameters = new List<Tuple<string, string>>();
            bool setDefaultBundleTotal = true;

            // Extract the continuation token, filter out the other known query parameters that's not search related.
            foreach (Tuple<string, string> query in queryParameters ?? Enumerable.Empty<Tuple<string, string>>())
            {
                if (query.Item1 == KnownQueryParameterNames.ContinuationToken)
                {
                    // This is an unreachable case. The mapping of the query parameters makes it so only one continuation token can exist.
                    if (continuationToken != null)
                    {
                        throw new InvalidSearchOperationException(
                            string.Format(Core.Resources.MultipleQueryParametersNotAllowed, KnownQueryParameterNames.ContinuationToken));
                    }

                    try
                    {
                        continuationToken = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(query.Item2));
                    }
                    catch (FormatException)
                    {
                        throw new BadRequestException(Core.Resources.InvalidContinuationToken);
                    }

                    setDefaultBundleTotal = false;
                }
                else if (query.Item1 == KnownQueryParameterNames.Format)
                {
                    // TODO: We need to handle format parameter.
                }
                else if (string.IsNullOrWhiteSpace(query.Item1) || string.IsNullOrWhiteSpace(query.Item2))
                {
                    // Query parameter with empty value is not supported.
                    unsupportedSearchParameters.Add(query);
                }
                else if (string.Compare(query.Item1, KnownQueryParameterNames.Total, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    if (Enum.TryParse<TotalType>(query.Item2, true, out var totalType))
                    {
                        ValidateTotalType(totalType);

                        searchOptions.IncludeTotal = totalType;
                        setDefaultBundleTotal = false;
                    }
                    else
                    {
                        throw new BadRequestException(string.Format(Core.Resources.InvalidTotalParameter, query.Item2, SupportedTotalTypes));
                    }
                }
                else
                {
                    // Parse the search parameters.
                    try
                    {
                        // Basic format checking (e.g. integer value for _count key etc.).
                        searchParams.Add(query.Item1, query.Item2);
                    }
                    catch (Exception ex)
                    {
                        throw new BadRequestException(ex.Message);
                    }
                }
            }

            searchOptions.ContinuationToken = continuationToken;

            if (setDefaultBundleTotal)
            {
                ValidateTotalType(_featureConfiguration.IncludeTotalInBundle);
                searchOptions.IncludeTotal = _featureConfiguration.IncludeTotalInBundle;
            }

            // Check the item count.
            if (searchParams.Count != null)
            {
                if (searchParams.Count > _featureConfiguration.MaxItemCountPerSearch)
                {
                    throw new BadRequestException(string.Format(Core.Resources.SearchParamaterCountExceedLimit, _featureConfiguration.MaxItemCountPerSearch, searchParams.Count));
                }

                searchOptions.MaxItemCount = searchParams.Count.Value;
            }
            else
            {
                searchOptions.MaxItemCount = _featureConfiguration.DefaultItemCountPerSearch;
            }

            searchOptions.IncludeCount = _featureConfiguration.DefaultIncludeCountPerSearch;

            // Check to see if only the count should be returned
            searchOptions.CountOnly = searchParams.Summary == SummaryType.Count;

            // If the resource type is not specified, then the common
            // search parameters should be used.
            ResourceType parsedResourceType = ResourceType.DomainResource;

            if (!string.IsNullOrWhiteSpace(resourceType) &&
                !Enum.TryParse(resourceType, out parsedResourceType))
            {
                throw new ResourceNotSupportedException(resourceType);
            }

            var searchExpressions = new List<Expression>();

            if (!string.IsNullOrWhiteSpace(resourceType))
            {
                searchExpressions.Add(Expression.SearchParameter(_resourceTypeSearchParameter, Expression.StringEquals(FieldName.TokenCode, null, resourceType, false)));
            }

            searchExpressions.AddRange(searchParams.Parameters.Select(
                    q =>
                    {
                        try
                        {
                            return _expressionParser.Parse(parsedResourceType.ToString(), q.Item1, q.Item2);
                        }
                        catch (SearchParameterNotSupportedException)
                        {
                            unsupportedSearchParameters.Add(q);

                            return null;
                        }
                    })
                .Where(item => item != null));

            if (searchParams.Include?.Count > 0)
            {
                searchExpressions.AddRange(searchParams.Include.Select(
                    q => _expressionParser.ParseInclude(parsedResourceType.ToString(), q, false /* not reversed */, false))
                    .Where(item => item != null));
            }

            if (searchParams.RevInclude?.Count > 0)
            {
                searchExpressions.AddRange(searchParams.RevInclude.Select(
                    q => _expressionParser.ParseInclude(parsedResourceType.ToString(), q, true /* reversed */, false))
                    .Where(item => item != null));
            }

            // Parse _include:iterate (_include:recurse) parameters.
            // :iterate (:recurse) modifiers are not supported by Hl7.Fhir.Rest, hence not added to the Include collection and exist in the Parameters list.
            // See https://github.com/FirelyTeam/fhir-net-api/issues/222
            // _include:iterate (_include:recurse) expression may appear without a preceding _include parameter
            // when applied on a circular reference
            searchExpressions.AddRange(ParseIncludeIterateExpressions(searchParams, false));
            searchExpressions.AddRange(ParseIncludeIterateExpressions(searchParams, true /* reversed */));

            if (!string.IsNullOrWhiteSpace(compartmentType))
            {
                if (Enum.TryParse(compartmentType, out CompartmentType parsedCompartmentType))
                {
                    if (string.IsNullOrWhiteSpace(compartmentId))
                    {
                        throw new InvalidSearchOperationException(Core.Resources.CompartmentIdIsInvalid);
                    }

                    searchExpressions.Add(Expression.CompartmentSearch(compartmentType, compartmentId));
                }
                else
                {
                    throw new InvalidSearchOperationException(string.Format(Core.Resources.CompartmentTypeIsInvalid, compartmentType));
                }
            }

            if (searchExpressions.Count == 1)
            {
                searchOptions.Expression = searchExpressions[0];
            }
            else if (searchExpressions.Count > 1)
            {
                searchOptions.Expression = Expression.And(searchExpressions.ToArray());
            }

            if (unsupportedSearchParameters.Any())
            {
                // TODO: Client can specify whether exception should be raised or not when it encounters unknown search parameters.
                // For now, we will ignore any unknown search parameters.
            }

            searchOptions.UnsupportedSearchParams = unsupportedSearchParameters;

            if (searchParams.Sort?.Count > 0)
            {
                var sortings = new List<(SearchParameterInfo, SortOrder)>();
                List<(string parameterName, string reason)> unsupportedSortings = null;

                foreach (Tuple<string, Hl7.Fhir.Rest.SortOrder> sorting in searchParams.Sort)
                {
                    try
                    {
                        SearchParameterInfo searchParameterInfo = _searchParameterDefinitionManager.GetSearchParameter(parsedResourceType.ToString(), sorting.Item1);

                        if (searchParameterInfo.IsSortSupported())
                        {
                            sortings.Add((searchParameterInfo, sorting.Item2.ToCoreSortOrder()));
                        }
                        else
                        {
                            throw new SearchParameterNotSupportedException(string.Format(Core.Resources.SearchSortParameterNotSupported, searchParameterInfo.Name));
                        }
                    }
                    catch (SearchParameterNotSupportedException)
                    {
                        (unsupportedSortings ??= new List<(string parameterName, string reason)>()).Add((sorting.Item1, string.Format(Core.Resources.SearchSortParameterNotSupported, sorting.Item1)));
                    }
                }

                searchOptions.Sort = sortings;
                searchOptions.UnsupportedSortingParams = (IReadOnlyList<(string parameterName, string reason)>)unsupportedSortings ?? Array.Empty<(string parameterName, string reason)>();
            }
            else
            {
                searchOptions.Sort = Array.Empty<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)>();
                searchOptions.UnsupportedSortingParams = Array.Empty<(string parameterName, string reason)>();
            }

            return searchOptions;

            IEnumerable<IncludeExpression> ParseIncludeIterateExpressions(SearchParams searchParams, bool reversed)
            {
                var iterateModifiers = reversed ? RevIncludeIterateModifiers : IncludeIterateModifiers;

                return searchParams.Parameters
                .Where(p => p != null && iterateModifiers.Where(m => string.Equals(p.Item1, m, StringComparison.OrdinalIgnoreCase)).Any())
                .Select(p =>
                {
                    ResourceType parsedIncludeResourceType = ResourceType.DomainResource;
                    var includeResourceType = p.Item2.Split(':')[0];
                    if (!string.IsNullOrWhiteSpace(includeResourceType) &&
                        !Enum.TryParse(includeResourceType, out parsedIncludeResourceType))
                    {
                        throw new ResourceNotSupportedException(includeResourceType);
                    }

                    var expression = _expressionParser.ParseInclude(parsedIncludeResourceType.ToString(), p.Item2, reversed, true);

                    // Reversed Iterate expressions (not wildcard) must specify target type if there is more than one possible target type
                    if (expression.Reversed && expression.Iterate && expression.TargetResourceType == null && expression.ReferenceSearchParameter?.TargetResourceTypes?.Count > 1)
                    {
                        throw new BadRequestException(string.Format(Core.Resources.RevIncludeIterateTargetTypeNotSpecified, p.Item2));
                    }

                    return expression;
                });
            }

            void ValidateTotalType(TotalType totalType)
            {
                // Estimate is not yet supported.
                if (totalType == TotalType.Estimate)
                {
                    throw new SearchOperationNotSupportedException(string.Format(Core.Resources.UnsupportedTotalParameter, totalType, SupportedTotalTypes));
                }
            }
        }
    }
}
