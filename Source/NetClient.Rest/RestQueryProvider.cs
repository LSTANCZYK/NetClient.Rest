﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace NetClient.Rest
{
    /// <summary>
    ///     Defines methods to create and execute queries.
    /// </summary>
    /// <typeparam name="T">The type of element to query.</typeparam>
    internal class RestQueryProvider<T> : IQueryProvider
    {
        private readonly IElement<T> element;

        /// <summary>
        ///     Initializes a new instance of the <see cref="RestQueryProvider{T}" /> class.
        /// </summary>
        /// <param name="element">The element.</param>
        public RestQueryProvider(IElement<T> element)
        {
            this.element = element;
        }

        private Resource<T> Resource => element as Resource<T>;

        private bool ContainsPlaceHolder(string item)
        {
            return item.Contains("{") && item.Contains("}");
        }

        private async Task<TResult> GetRestValueAsync<TResult>(Expression expression)
        {
            var result = JsonConvert.DeserializeObject<TResult>($"[]");
            var resourceValues = new RestQueryTranslator().GetResourceValues(expression);
            var routeTemplates = Resource?.GetRouteTemplates();
            var parameterTemplates = Resource?.GetParameterTemplates();

            if (routeTemplates == null || !routeTemplates.Any())
            {
                element.OnError?.Invoke(new InvalidOperationException("Unable to obtain a value from the service because a route was not specified."));
                return result;
            }

            if (Resource?.BaseUri == null)
            {
                element.OnError?.Invoke(new InvalidOperationException("Unable to obtain a value from the service because a base URI was not specified."));
                return result;
            }

            // Calculate routes.
            ReplacePlaceHolders(routeTemplates, resourceValues);
            var path = routeTemplates.FirstOrDefault(r => !ContainsPlaceHolder(r));
            if (string.IsNullOrWhiteSpace(path))
            {
                element.OnError?.Invoke(new InvalidOperationException($"Route resolution has failed. You probably failed to provide an appropriate route attribute with valid route templates."));
                return result;
            }

            // Calculate parameters.
            ReplacePlaceHolders(parameterTemplates, resourceValues);
            var parameters = string.Empty;
            parameters = parameterTemplates.Aggregate(parameters, (seed, accumulate) => ContainsPlaceHolder(accumulate) ? seed : $"{seed}&{accumulate}").TrimStart('&');

            // Calculate the request URI.
            var uriString = $"{Resource.BaseUri.AbsoluteUri}{path}";
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                uriString = $"{uriString}?{parameters}";
            }

            using (var client = new HttpClient())
            {
                var requestUri = new Uri(uriString);
                using (var response = await client.GetAsync(requestUri))
                {
                    using (var content = response.Content)
                    {
                        var json = await content.ReadAsStringAsync();
                        if (string.IsNullOrWhiteSpace(json)) return result;

                        if (Resource?.SerializerSettings == null)
                        {
                            result = JsonConvert.DeserializeObject<TResult>($"[{json}]");
                        }
                        else
                        {
                            result = JsonConvert.DeserializeObject<TResult>($"[{json}]", Resource.SerializerSettings);
                        }
                    }
                }
            }
            return result;
        }

        private void ReplacePlaceHolders(string[] source, IDictionary<string, object> resourceValues)
        {
            foreach (var index in Enumerable.Range(0, source.Length))
            {
                source[index] = resourceValues.Aggregate(source[index], (seed, accumulate) => seed.Replace($"{{{accumulate.Key}}}", accumulate.Value.ToString())).TrimStart('/');
            }
        }

        /// <summary>
        ///     Creates the query.
        /// </summary>
        /// <param name="expression">The expression.</param>
        /// <returns>IQueryable.</returns>
        public IQueryable CreateQuery(Expression expression)
        {
            return new Resource<T>(element.Client, Resource.Property, element.OnError, expression);
        }

        /// <summary>
        ///     Creates the query.
        /// </summary>
        /// <typeparam name="TElement">The type of the element.</typeparam>
        /// <param name="expression">The expression.</param>
        /// <returns>IQueryable&lt;TElement&gt;.</returns>
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        {
            return (IQueryable<TElement>) new Resource<T>(element.Client, Resource.Property, element.OnError, expression);
        }

        /// <summary>
        ///     Executes the specified expression.
        /// </summary>
        /// <param name="expression">The expression.</param>
        /// <returns>System.Object.</returns>
        public object Execute(Expression expression)
        {
            return Execute<Resource<T>>(expression);
        }

        /// <summary>
        ///     Executes the specified expression.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="expression">The expression.</param>
        /// <returns>TResult</returns>
        public TResult Execute<TResult>(Expression expression)
        {
            var result = JsonConvert.DeserializeObject<TResult>($"[]");

            GetRestValueAsync<TResult>(expression).ContinueWith(task =>
            {
                if (task.IsCompleted && !task.IsFaulted)
                {
                    result = task.Result;
                }
                else
                {
                    element.OnError?.Invoke(task.Exception);
                }
            }).Wait();

            return result;
        }
    }
}