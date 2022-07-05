﻿namespace WishList.Data
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;
    using WishList.Models;
    using WishList.Services;
    using Microsoft.AspNetCore.Http;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System.Web;
    using Vtex.Api.Context;

    /// <summary>
    /// Concrete implementation of <see cref="IWishListRepository"/> for persisting data to/from Masterdata v2.
    /// </summary>
    public class WishListRepository : IWishListRepository
    {
        private readonly IVtexEnvironmentVariableProvider _environmentVariableProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IHttpClientFactory _clientFactory;
        private readonly IIOServiceContext _context;
        private readonly string _applicationName;
        private static string _tokenResponse;
        public static string tokenResponse 
        {
            get
            {
                return _tokenResponse;
            }
            set
            {
                _tokenResponse = value;
            }
        }

        public WishListRepository(IVtexEnvironmentVariableProvider environmentVariableProvider, IHttpContextAccessor httpContextAccessor, IHttpClientFactory clientFactory, IIOServiceContext context)
        {
            this._environmentVariableProvider = environmentVariableProvider ??
                                                throw new ArgumentNullException(nameof(environmentVariableProvider));

            this._httpContextAccessor = httpContextAccessor ??
                                        throw new ArgumentNullException(nameof(httpContextAccessor));

            this._clientFactory = clientFactory ??
                               throw new ArgumentNullException(nameof(clientFactory));

            this._context = context ??
                               throw new ArgumentNullException(nameof(context));

            this._applicationName =
                $"{this._environmentVariableProvider.ApplicationVendor}.{this._environmentVariableProvider.ApplicationName}";

            this.VerifySchema().Wait();
        }

        public async Task<bool> SaveWishList(IList<ListItem> listItems, string shopperId, string listName, bool? isPublic, string documentId)
        {
            // PATCH https://{{accountName}}.vtexcommercestable.com.br/api/dataentities/{{data_entity_name}}/documents

            await this.VerifySchema();
            if (listItems == null)
            {
                listItems = new List<ListItem>();
            }

            ListItemsWrapper listItemsWrapper = new ListItemsWrapper
            {
                ListItems = listItems,
                IsPublic = isPublic,
                Name = listName
            };

            WishListWrapper wishListWrapper = new WishListWrapper
            {
                Id = documentId,
                Email = shopperId,
                ListItemsWrapper = new List<ListItemsWrapper> { listItemsWrapper }
            };

            var jsonSerializedListItems = JsonConvert.SerializeObject(wishListWrapper);
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Patch,
                RequestUri = new Uri($"http://{this._httpContextAccessor.HttpContext.Request.Headers[WishListConstants.VTEX_ACCOUNT_HEADER_NAME]}.vtexcommercestable.com.br/api/dataentities/{WishListConstants.DATA_ENTITY}/documents"),
                Content = new StringContent(jsonSerializedListItems, Encoding.UTF8, WishListConstants.APPLICATION_JSON)
            };

            string authToken = _context.Vtex.AuthToken;
            if (authToken != null)
            {
                request.Headers.Add(WishListConstants.AUTHORIZATION_HEADER_NAME, authToken);
                request.Headers.Add(WishListConstants.VtexIdCookie, authToken);
                request.Headers.Add(WishListConstants.PROXY_AUTHORIZATION_HEADER_NAME, authToken);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();
            _context.Vtex.Logger.Debug("SaveWishList", null, $"'{shopperId}' '{listName}' '{documentId}' [{response.StatusCode}]\n{responseContent}");

            return response.IsSuccessStatusCode;
        }

        public async Task<ResponseListWrapper> GetWishList(string shopperId)
        {
            // GET https://{{accountName}}.vtexcommercestable.com.br/api/dataentities/{{data_entity_name}}/documents/{{id}}
            // GET https://{{accountName}}.vtexcommercestable.com.br/api/dataentities/{{data_entity_name}}/search

            ResponseListWrapper responseListWrapper = new ResponseListWrapper();
            if (string.IsNullOrEmpty(shopperId)) {
                _context.Vtex.Logger.Warn("GetWishList", null, $"Nonvalid shopperId");
                return responseListWrapper;
            }

            await this.VerifySchema();
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri($"http://{this._httpContextAccessor.HttpContext.Request.Headers[WishListConstants.VTEX_ACCOUNT_HEADER_NAME]}.vtexcommercestable.com.br/api/dataentities/{WishListConstants.DATA_ENTITY}/search?_fields=id,email,ListItemsWrapper&_schema={WishListConstants.SCHEMA}&email={HttpUtility.UrlEncode(shopperId)}")
            };

            string authToken = _context.Vtex.AuthToken;
            if (authToken != null)
            {
                request.Headers.Add(WishListConstants.AUTHORIZATION_HEADER_NAME, authToken);
                request.Headers.Add(WishListConstants.VtexIdCookie, authToken);
                request.Headers.Add(WishListConstants.PROXY_AUTHORIZATION_HEADER_NAME, authToken);
            }
            
            request.Headers.Add("Cache-Control", "no-cache");

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();
            _context.Vtex.Logger.Debug("GetWishList", null, $"'{shopperId}' [{response.StatusCode}]\n{responseContent}");
            try
            {
                JArray searchResult = JArray.Parse(responseContent);
                for(int l = 0; l < searchResult.Count; l++)
                {
                    JToken listWrapper = searchResult[l];
                    if(l == 0)
                    {
                        responseListWrapper = JsonConvert.DeserializeObject<ResponseListWrapper>(listWrapper.ToString());
                    }
                    else
                    {
                        ResponseListWrapper listToRemove = JsonConvert.DeserializeObject<ResponseListWrapper>(listWrapper.ToString());
                        bool removed = await this.DeleteWishList(listToRemove.Id);
                    }
                }
            }
            catch(Exception ex)
            {
                responseListWrapper.message = $"Error:{ex.Message}: Rsp = {responseContent} ";
                _context.Vtex.Logger.Error("GetWishList", null, $"Error getting list for {shopperId}", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                responseListWrapper.message = $"Get:{response.StatusCode}: Rsp = {responseContent}";
            }

            return responseListWrapper;
        }

        public async Task<bool> DeleteWishList(string documentId)
        {
            // DEL https://{{accountName}}.vtexcommercestable.com.br/api/dataentities/{{data_entity_name}}/documents/{{id}}
            await this.VerifySchema();
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Delete,
                RequestUri = new Uri($"http://{this._httpContextAccessor.HttpContext.Request.Headers[WishListConstants.VTEX_ACCOUNT_HEADER_NAME]}.vtexcommercestable.com.br/api/dataentities/{WishListConstants.DATA_ENTITY}/documents/{documentId}")
            };

            string authToken = _context.Vtex.AuthToken;
            if (authToken != null)
            {
                request.Headers.Add(WishListConstants.AUTHORIZATION_HEADER_NAME, authToken);
                request.Headers.Add(WishListConstants.VtexIdCookie, authToken);
                request.Headers.Add(WishListConstants.PROXY_AUTHORIZATION_HEADER_NAME, authToken);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();

            return response.IsSuccessStatusCode;
        }

        public async Task VerifySchema()
        {
            // https://{{accountName}}.vtexcommercestable.com.br/api/dataentities/{{data_entity_name}}/schemas/{{schema_name}}
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri($"http://{this._httpContextAccessor.HttpContext.Request.Headers[WishListConstants.VTEX_ACCOUNT_HEADER_NAME]}.vtexcommercestable.com.br/api/dataentities/{WishListConstants.DATA_ENTITY}/schemas/{WishListConstants.SCHEMA}")
            };

            string authToken = _context.Vtex.AuthToken;
            if (authToken != null)
            {
                request.Headers.Add(WishListConstants.AUTHORIZATION_HEADER_NAME, authToken);
                request.Headers.Add(WishListConstants.VtexIdCookie, authToken);
                request.Headers.Add(WishListConstants.PROXY_AUTHORIZATION_HEADER_NAME, authToken);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();
            //Console.WriteLine($"Verifying Schema [{response.StatusCode}] {responseContent.Equals(WishListConstants.SCHEMA_JSON)}");
            _context.Vtex.Logger.Debug("VerifySchema", null, $"Verifying Schema [{response.StatusCode}] {responseContent.Equals(WishListConstants.SCHEMA_JSON)}");
            if (response.IsSuccessStatusCode)
            {
                if (responseContent.Equals(WishListConstants.SCHEMA_JSON))
                {
                    _context.Vtex.Logger.Debug("VerifySchema", null, "Schema Verified.");
                }
                else
                {
                    _context.Vtex.Logger.Debug("VerifySchema", null, $"Schema does not match.\n{responseContent}");
                    request = new HttpRequestMessage
                    {
                        Method = HttpMethod.Put,
                        RequestUri = new Uri($"http://{this._httpContextAccessor.HttpContext.Request.Headers[WishListConstants.VTEX_ACCOUNT_HEADER_NAME]}.vtexcommercestable.com.br/api/dataentities/{WishListConstants.DATA_ENTITY}/schemas/{WishListConstants.SCHEMA}"),
                        Content = new StringContent(WishListConstants.SCHEMA_JSON, Encoding.UTF8, WishListConstants.APPLICATION_JSON)
                    };
                    
                    if (authToken != null)
                    {
                        request.Headers.Add(WishListConstants.AUTHORIZATION_HEADER_NAME, authToken);
                        request.Headers.Add(WishListConstants.VtexIdCookie, authToken);
                        request.Headers.Add(WishListConstants.PROXY_AUTHORIZATION_HEADER_NAME, authToken);
                    }

                    response = await client.SendAsync(request);
                    responseContent = await response.Content.ReadAsStringAsync();
                    //Console.WriteLine($"Applying Schema [{response.StatusCode}] {responseContent}");
                    _context.Vtex.Logger.Debug("VerifySchema", null, $"Applying Schema [{response.StatusCode}] {responseContent}");
                }
            }
        }

        private async Task<string> FirstScroll()
        {
            var client = _clientFactory.CreateClient();
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri($"http://{this._httpContextAccessor.HttpContext.Request.Headers[WishListConstants.VTEX_ACCOUNT_HEADER_NAME]}.vtexcommercestable.com.br/api/dataentities/{WishListConstants.DATA_ENTITY}/scroll?_size=200&_fields=email,ListItemsWrapper")
            };

            string authToken = this._httpContextAccessor.HttpContext.Request.Headers[WishListConstants.HEADER_VTEX_CREDENTIAL];
            if (authToken != null)
            {
                request.Headers.Add(WishListConstants.AUTHORIZATION_HEADER_NAME, authToken);
                request.Headers.Add(WishListConstants.VtexIdCookie, authToken);
                request.Headers.Add(WishListConstants.PROXY_AUTHORIZATION_HEADER_NAME, authToken);
            }
            request.Headers.Add("Cache-Control", "no-cache");
            var response = await client.SendAsync(request);
            tokenResponse = response.Headers.GetValues("X-VTEX-MD-TOKEN").FirstOrDefault();

            string responseContent = await response.Content.ReadAsStringAsync();
            _context.Vtex.Logger.Debug("GetAllLists", null, $"[{response.StatusCode}]");

            return responseContent;
        }

        private async Task<string> SubScroll()
        {
            var client = _clientFactory.CreateClient();
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri($"http://{this._httpContextAccessor.HttpContext.Request.Headers[WishListConstants.VTEX_ACCOUNT_HEADER_NAME]}.vtexcommercestable.com.br/api/dataentities/{WishListConstants.DATA_ENTITY}/scroll?_token={tokenResponse}")
            };

            string authToken = this._httpContextAccessor.HttpContext.Request.Headers[WishListConstants.HEADER_VTEX_CREDENTIAL];
            if (authToken != null)
            {
                request.Headers.Add(WishListConstants.AUTHORIZATION_HEADER_NAME, authToken);
                request.Headers.Add(WishListConstants.VtexIdCookie, authToken);
                request.Headers.Add(WishListConstants.PROXY_AUTHORIZATION_HEADER_NAME, authToken);
            }
            request.Headers.Add("Cache-Control", "no-cache");
            var response = await client.SendAsync(request);

            string responseContent = await response.Content.ReadAsStringAsync();
            _context.Vtex.Logger.Debug("GetAllLists", null, $"[{response.StatusCode}]");

            return responseContent;
        }
        public async Task<WishListsWrapper> GetAllLists()
        {
            await this.VerifySchema();
            var i = 0;
            var status = true;
            JArray searchResult = new JArray();

            while (status)
            {
                if( i == 0)
                {
                    var res = await FirstScroll();
                    JArray resArray = JArray.Parse(res);
                    searchResult.Merge(resArray);
                }
                else
                {
                    var res = await SubScroll();
                    JArray resArray = JArray.Parse(res);
                    if (resArray.Count < 200) 
                    {
                        status = false;
                    }
                    searchResult.Merge(resArray);
                }
                i++;
            }
            
            WishListsWrapper wishListsWrapper = new WishListsWrapper();
            wishListsWrapper.WishLists = new List<WishListWrapper>();
            WishListWrapper responseListWrapper = new WishListWrapper();

            try
            {
                for (int l = 0; l < searchResult.Count; l++)
                {
                    JToken listWrapper = searchResult[l];
                    if (listWrapper != null)
                    {
                        responseListWrapper = JsonConvert.DeserializeObject<WishListWrapper>(listWrapper.ToString());
                        if (responseListWrapper != null)
                        {
                            wishListsWrapper.WishLists.Add(responseListWrapper);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _context.Vtex.Logger.Error("GetAllLists", null, "Error getting lists", ex);
            }

            return wishListsWrapper;
        }
    }
}
