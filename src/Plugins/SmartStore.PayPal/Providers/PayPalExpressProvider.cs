using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using SmartStore.Core;
using SmartStore.Core.Domain.Customers;
using SmartStore.Core.Domain.Orders;
using SmartStore.Core.Domain.Payments;
using SmartStore.Core.Domain.Shipping;
using SmartStore.Core.Infrastructure;
using SmartStore.Core.Plugins;
using SmartStore.PayPal.Controllers;
using SmartStore.PayPal.PayPalSvc;
using SmartStore.PayPal.Services;
using SmartStore.PayPal.Settings;
using SmartStore.Services.Catalog;
using SmartStore.Services.Common;
using SmartStore.Services.Customers;
using SmartStore.Services.Directory;
using SmartStore.Services.Orders;
using SmartStore.Services.Payments;
using SmartStore.Services.Shipping;

namespace SmartStore.PayPal
{
	[SystemName("Payments.PayPalExpress")]
    [FriendlyName("PayPal Express")]
    [DisplayOrder(0)]
    public partial class PayPalExpressProvider : PayPalProviderBase<PayPalExpressPaymentSettings>
    {
        private readonly ICurrencyService _currencyService;
        private readonly IPriceCalculationService _priceCalculationService;
        private readonly IGenericAttributeService _genericAttributeService;
        private readonly IStateProvinceService _stateProvinceService;
        private readonly IGiftCardService _giftCardService;
        private readonly IShippingService _shippingService;
        private readonly ICustomerService _customerService;
        private readonly ICountryService _countryService;
        private readonly HttpContextBase _httpContext;

        public PayPalExpressProvider(
            ICurrencyService currencyService,
            IPriceCalculationService priceCalculationService,
            IGenericAttributeService genericAttributeService,
            IStateProvinceService stateProvinceService,
            IGiftCardService giftCardService,
            IShippingService shippingService,
            ICustomerService customerService,
            ICountryService countryService,
            HttpContextBase httpContext)
        {
            _currencyService = currencyService;
            _priceCalculationService = priceCalculationService;
            _genericAttributeService = genericAttributeService;
            _stateProvinceService = stateProvinceService;
            _giftCardService = giftCardService;
            _shippingService = shippingService;
            _customerService = customerService;
            _countryService = countryService;
            _httpContext = httpContext;
        }

		public static string SystemName { get { return "Payments.PayPalExpress"; } }

		public override PaymentMethodType PaymentMethodType
		{
			get
			{
				return PaymentMethodType.StandardAndButton;
			}
		}

		private PaymentActionCodeType GetPaymentAction(PayPalExpressPaymentSettings settings)
		{
			if (settings.TransactMode == TransactMode.Authorize)
			{
				return PaymentActionCodeType.Authorization;
			}
			else
			{
				return PaymentActionCodeType.Sale;
			}
		}

		protected override string GetResourceRootKey()
		{
			return "Plugins.Payments.PayPalExpress";
		}

        /// <summary>
        /// Process a payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public override ProcessPaymentResult ProcessPayment(ProcessPaymentRequest processPaymentRequest)
        {
			var result = new ProcessPaymentResult();
			var settings = Services.Settings.LoadSetting<PayPalExpressPaymentSettings>(processPaymentRequest.StoreId);

			var doPayment = DoExpressCheckoutPayment(processPaymentRequest);
            
            if (doPayment.Ack == AckCodeType.Success)
            {
				if (GetPaymentAction(settings) == PaymentActionCodeType.Authorization)
                {
					result.AuthorizationTransactionId = doPayment.DoExpressCheckoutPaymentResponseDetails.PaymentInfo.FirstOrDefault().TransactionID;
					result.AuthorizationTransactionResult = doPayment.Ack.ToString();

					result.NewPaymentStatus = PaymentStatus.Authorized;
				}
				else
                {
					result.CaptureTransactionId = doPayment.DoExpressCheckoutPaymentResponseDetails.PaymentInfo.FirstOrDefault().TransactionID;
					result.CaptureTransactionResult = doPayment.Ack.ToString();

					result.NewPaymentStatus = PaymentStatus.Paid;
                }

				//result.AuthorizationTransactionId = processPaymentRequest.PaypalToken;
				//result.CaptureTransactionId = doPayment.DoExpressCheckoutPaymentResponseDetails.PaymentInfo.FirstOrDefault().TransactionID;
				//result.CaptureTransactionResult = doPayment.Ack.ToString();
			}
            else
            {
                result.NewPaymentStatus = PaymentStatus.Pending;

				result.Errors.Each(x => result.AddError(x));
			}

            return result;
        }

        /// <summary>
        /// Process recurring payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public override ProcessPaymentResult ProcessRecurringPayment(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new ProcessPaymentResult();
            //TODO
            return result;
        }

        protected override string GetControllerName()
        {
            return "PayPalExpress";
        }

        public override Type GetControllerType()
        {
            return typeof(PayPalExpressController);
        }

        public SetExpressCheckoutResponseType SetExpressCheckout(PayPalProcessPaymentRequest processPaymentRequest, IList<OrganizedShoppingCartItem> cart)
        {
            var result = new SetExpressCheckoutResponseType();
			var store = Services.StoreService.GetStoreById(processPaymentRequest.StoreId);
			var settings = Services.Settings.LoadSetting<PayPalExpressPaymentSettings>(processPaymentRequest.StoreId);
			var payPalCurrency = GetApiCurrency(store.PrimaryStoreCurrency);
            
            var req = new SetExpressCheckoutReq
            {
                SetExpressCheckoutRequest = new SetExpressCheckoutRequestType
                {
                    Version = ApiVersion,
                    SetExpressCheckoutRequestDetails = new SetExpressCheckoutRequestDetailsType()
                }
            };

            var details = new SetExpressCheckoutRequestDetailsType
            {
                PaymentAction = GetPaymentAction(settings),
                PaymentActionSpecified = true,
                CancelURL = Services.WebHelper.GetStoreLocation(store.SslEnabled) + "cart",
                ReturnURL = Services.WebHelper.GetStoreLocation(store.SslEnabled) + "Plugins/SmartStore.PayPal/PayPalExpress/GetDetails",
                //CallbackURL = _webHelper.GetStoreLocation(currentStore.SslEnabled) + "Plugins/SmartStore.PayPal/PayPalExpress/ShippingOptions?CustomerID=" + _workContext.CurrentCustomer.Id.ToString(),
                //CallbackTimeout = _payPalExpressPaymentSettings.CallbackTimeout.ToString() 
                ReqConfirmShipping = settings.ConfirmedShipment.ToString(),
                NoShipping = settings.NoShipmentAddress.ToString()
            };

            // populate cart
            decimal itemTotal = decimal.Zero;
            var cartItems = new List<PaymentDetailsItemType>();
            foreach (OrganizedShoppingCartItem item in cart)
            {
                decimal shoppingCartUnitPriceWithDiscountBase = _priceCalculationService.GetUnitPrice(item, true);
                decimal shoppingCartUnitPriceWithDiscount = _currencyService.ConvertFromPrimaryStoreCurrency(shoppingCartUnitPriceWithDiscountBase, Services.WorkContext.WorkingCurrency);
                decimal priceIncludingTier = shoppingCartUnitPriceWithDiscount;

                cartItems.Add(new PaymentDetailsItemType()
                {
                    Name = item.Item.Product.Name,
                    Number = item.Item.Product.Sku,
                    Quantity = item.Item.Quantity.ToString(),
                    Amount = new BasicAmountType()  // this is the per item cost
                    {
						currencyID = payPalCurrency,
                        Value = (priceIncludingTier).ToString("N", new CultureInfo("en-us"))
                    }
                });
                itemTotal += (item.Item.Quantity * priceIncludingTier);
            };

            // additional handling fee
            var additionalHandlingFee = GetAdditionalHandlingFee(cart);
            cartItems.Add(new PaymentDetailsItemType()
            {
                Name = T("Plugins.Payments.PayPal.PaymentMethodFee").Text,
                Quantity = "1",
                Amount = new BasicAmountType()  
                {
					currencyID = payPalCurrency,
                    Value = (additionalHandlingFee).ToString("N", new CultureInfo("en-us"))
                }
            });
            itemTotal += GetAdditionalHandlingFee(cart);

            //shipping
            decimal shippingTotal = decimal.Zero;
            if (cart.RequiresShipping())
            {
                decimal? shoppingCartShippingBase = OrderTotalCalculationService.GetShoppingCartShippingTotal(cart);
                if (shoppingCartShippingBase.HasValue && shoppingCartShippingBase > 0)
                {
                    shippingTotal = _currencyService.ConvertFromPrimaryStoreCurrency(shoppingCartShippingBase.Value, Services.WorkContext.WorkingCurrency);
                }
                else
                {
                    shippingTotal = settings.DefaultShippingPrice;
                }
            }

            //This is the default if the Shipping Callback fails
            //var shippingOptions = new List<ShippingOptionType>();
            //shippingOptions.Add(new ShippingOptionType()
            //{
            //    ShippingOptionIsDefault = "true",
            //    ShippingOptionName = "Standard Shipping",
            //    ShippingOptionAmount = new BasicAmountType()
            //    {
            //        Value = shippingTotal.ToString(), //This is the default value used for shipping if the Instant Update API returns an error or does not answer within the callback time
            //        currencyID = PaypalHelper.GetPaypalCurrency(_currencyService.GetCurrencyById(_currencySettings.PrimaryStoreCurrencyId))
            //    }
            //});
            //details.FlatRateShippingOptions = shippingOptions.ToArray();
            //details.TotalType = TotalType.EstimatedTotal;

            // get total tax
            //SortedDictionary<decimal, decimal> taxRates = null;
            //decimal shoppingCartTaxBase = OrderTotalCalculationService.GetTaxTotal(cart, out taxRates);
            //decimal shoppingCartTax = _currencyService.ConvertFromPrimaryStoreCurrency(shoppingCartTaxBase, CommonServices.WorkContext.WorkingCurrency);
            
            // discount
            decimal discount = -processPaymentRequest.Discount;

            if (discount != 0)
            {
                cartItems.Add(new PaymentDetailsItemType()
                {
                    Name = T("Plugins.Payments.PayPal.ThreadrockDiscount").Text,
                    Quantity = "1",
                    Amount = new BasicAmountType() // this is the total discount
                    {
						currencyID = payPalCurrency,
                        Value = discount.ToString("N", new CultureInfo("en-us"))
                    }
                });

                itemTotal += discount;
            }

            // get customer
            int customerId = Convert.ToInt32(Services.WorkContext.CurrentCustomer.Id.ToString());
            var customer = _customerService.GetCustomerById(customerId);

            if (!cart.IsRecurring())
            {
                //we don't apply gift cards for recurring products
                var giftCards = _giftCardService.GetActiveGiftCardsAppliedByCustomer(customer);
                if (giftCards != null)
                {
                    foreach (var gc in giftCards)
                    {
                        if (itemTotal > decimal.Zero)
                        {
                            decimal remainingAmount = gc.GetGiftCardRemainingAmount();
                            decimal amountCanBeUsed = decimal.Zero;
                            if (itemTotal > remainingAmount)
                                amountCanBeUsed = remainingAmount;
                            else
                                amountCanBeUsed = itemTotal - .01M;

                            decimal amountToSubtract = -amountCanBeUsed;

                            cartItems.Add(new PaymentDetailsItemType()
                            {
                                Name = T("Plugins.Payments.PayPal.GiftcardApplied").Text,
                                Quantity = "1",
                                Amount = new BasicAmountType()
                                {
									currencyID = payPalCurrency,
                                    Value = amountToSubtract.ToString("N", new CultureInfo("en-us"))
                                }
                            });

                            //reduce subtotal
                            itemTotal += amountToSubtract;
                        }
                    }
                }
            }

            // populate payment details
            var paymentDetails = new PaymentDetailsType
            {
                ItemTotal = new BasicAmountType
                {
                    Value = Math.Round(itemTotal, 2).ToString("N", new CultureInfo("en-us")),
					currencyID = payPalCurrency
                },
                ShippingTotal = new BasicAmountType
                {
                    Value = Math.Round(shippingTotal, 2).ToString("N", new CultureInfo("en-us")),
					currencyID = payPalCurrency
                },
                //TaxTotal = new BasicAmountType
                //{
                //    Value = Math.Round(shoppingCartTax, 2).ToString("N", new CultureInfo("en-us")),
                //    currencyID = PayPalHelper.GetPaypalCurrency(_currencyService.GetCurrencyById(_currencySettings.PrimaryStoreCurrencyId))
                //},
                OrderTotal = new BasicAmountType
                {
                    Value = Math.Round(itemTotal + shippingTotal, 2).ToString("N", new CultureInfo("en-us")),
					currencyID = payPalCurrency
                },
                Custom = processPaymentRequest.OrderGuid.ToString(),
                ButtonSource = SmartStoreVersion.CurrentFullVersion,
                PaymentAction = GetPaymentAction(settings),
                PaymentDetailsItem = cartItems.ToArray()
            };
            details.PaymentDetails = new[] { paymentDetails };

            details.ShippingMethodSpecified = true;

            req.SetExpressCheckoutRequest.SetExpressCheckoutRequestDetails.Custom = processPaymentRequest.OrderGuid.ToString();
            req.SetExpressCheckoutRequest.SetExpressCheckoutRequestDetails = details;

            using (var service = GetApiAaService(settings))
            {
                result = service.SetExpressCheckout(req);
            }

            _httpContext.GetCheckoutState().CustomProperties.Add("PayPalExpressButtonUsed", true);
            return result;
        }

        public GetExpressCheckoutDetailsResponseType GetExpressCheckoutDetails(string token)
        {
            var result = new GetExpressCheckoutDetailsResponseType();
			var settings = Services.Settings.LoadSetting<PayPalExpressPaymentSettings>(Services.StoreContext.CurrentStore.Id);

            using (var service = GetApiAaService(settings))
            {
                var req = new GetExpressCheckoutDetailsReq();
                req.GetExpressCheckoutDetailsRequest = new GetExpressCheckoutDetailsRequestType
                {
                    Token = token,
                    Version = ApiVersion
                };

                result = service.GetExpressCheckoutDetails(req);
            }
            return result;
        }

        public ProcessPaymentRequest SetCheckoutDetails(ProcessPaymentRequest processPaymentRequest, GetExpressCheckoutDetailsResponseDetailsType checkoutDetails)
        {
            int customerId = Convert.ToInt32(Services.WorkContext.CurrentCustomer.Id.ToString());
            var customer = _customerService.GetCustomerById(customerId);
			var settings = Services.Settings.LoadSetting<PayPalExpressPaymentSettings>(Services.StoreContext.CurrentStore.Id);

            Services.WorkContext.CurrentCustomer = customer;

            //var cart = customer.ShoppingCartItems.Where(sci => sci.ShoppingCartType == ShoppingCartType.ShoppingCart).ToList();
            var cart = Services.WorkContext.CurrentCustomer.GetCartItems(ShoppingCartType.ShoppingCart, Services.StoreContext.CurrentStore.Id);

            // get/update billing address
            string billingFirstName = checkoutDetails.PayerInfo.PayerName.FirstName;
            string billingLastName = checkoutDetails.PayerInfo.PayerName.LastName;
            string billingEmail = checkoutDetails.PayerInfo.Payer;
            string billingAddress1 = checkoutDetails.PayerInfo.Address.Street1;
            string billingAddress2 = checkoutDetails.PayerInfo.Address.Street2;
            string billingPhoneNumber = checkoutDetails.PayerInfo.ContactPhone;
            string billingCity = checkoutDetails.PayerInfo.Address.CityName;
            int? billingStateProvinceId = null;
            var billingStateProvince = _stateProvinceService.GetStateProvinceByAbbreviation(checkoutDetails.PayerInfo.Address.StateOrProvince);
            if (billingStateProvince != null)
                billingStateProvinceId = billingStateProvince.Id;
            string billingZipPostalCode = checkoutDetails.PayerInfo.Address.PostalCode;
            int? billingCountryId = null;
            var billingCountry = _countryService.GetCountryByTwoLetterIsoCode(checkoutDetails.PayerInfo.Address.Country.ToString());
            if (billingCountry != null)
                billingCountryId = billingCountry.Id;

            var billingAddress = customer.Addresses.ToList().FindAddress(
                billingFirstName, billingLastName, billingPhoneNumber,
                billingEmail, string.Empty, string.Empty, billingAddress1, billingAddress2, billingCity,
                billingStateProvinceId, billingZipPostalCode, billingCountryId);

            if (billingAddress == null)
            {
                billingAddress = new Core.Domain.Common.Address()
                {
                    FirstName = billingFirstName,
                    LastName = billingLastName,
                    PhoneNumber = billingPhoneNumber,
                    Email = billingEmail,
                    FaxNumber = string.Empty,
                    Company = string.Empty,
                    Address1 = billingAddress1,
                    Address2 = billingAddress2,
                    City = billingCity,
                    StateProvinceId = billingStateProvinceId,
                    ZipPostalCode = billingZipPostalCode,
                    CountryId = billingCountryId,
                    CreatedOnUtc = DateTime.UtcNow,
                };
                customer.Addresses.Add(billingAddress);
            }

            //set default billing address
            customer.BillingAddress = billingAddress;
            _customerService.UpdateCustomer(customer);

            var genericAttributeService = EngineContext.Current.Resolve<IGenericAttributeService>();
            genericAttributeService.SaveAttribute<ShippingOption>(customer, SystemCustomerAttributeNames.SelectedShippingOption, null);

            bool shoppingCartRequiresShipping = cart.RequiresShipping();
            if (shoppingCartRequiresShipping)
            {
                var paymentDetails = checkoutDetails.PaymentDetails.FirstOrDefault();
                string[] shippingFullname = paymentDetails.ShipToAddress.Name.Trim().Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                string shippingFirstName = shippingFullname[0];
                string shippingLastName = string.Empty;
                if (shippingFullname.Length > 1)
                    shippingLastName = shippingFullname[1];
                string shippingEmail = checkoutDetails.PayerInfo.Payer;
                string shippingAddress1 = paymentDetails.ShipToAddress.Street1;
                string shippingAddress2 = paymentDetails.ShipToAddress.Street2;
                string shippingPhoneNumber = paymentDetails.ShipToAddress.Phone;
                string shippingCity = paymentDetails.ShipToAddress.CityName;
                int? shippingStateProvinceId = null;
                var shippingStateProvince = _stateProvinceService.GetStateProvinceByAbbreviation(paymentDetails.ShipToAddress.StateOrProvince);
                if (shippingStateProvince != null)
                    shippingStateProvinceId = shippingStateProvince.Id;
                int? shippingCountryId = null;
                string shippingZipPostalCode = paymentDetails.ShipToAddress.PostalCode;
                var shippingCountry = _countryService.GetCountryByTwoLetterIsoCode(paymentDetails.ShipToAddress.Country.ToString());
                if (shippingCountry != null)
                    shippingCountryId = shippingCountry.Id;

                var shippingAddress = customer.Addresses.ToList().FindAddress(
                    shippingFirstName, shippingLastName, shippingPhoneNumber,
                    shippingEmail, string.Empty, string.Empty,
                    shippingAddress1, shippingAddress2, shippingCity,
                    shippingStateProvinceId, shippingZipPostalCode, shippingCountryId);

                if (shippingAddress == null)
                {
                    shippingAddress = new Core.Domain.Common.Address()
                    {
                        FirstName = shippingFirstName,
                        LastName = shippingLastName,
                        PhoneNumber = shippingPhoneNumber,
                        Email = shippingEmail,
                        FaxNumber = string.Empty,
                        Company = string.Empty,
                        Address1 = shippingAddress1,
                        Address2 = shippingAddress2,
                        City = shippingCity,
                        StateProvinceId = shippingStateProvinceId,
                        ZipPostalCode = shippingZipPostalCode,
                        CountryId = shippingCountryId,
                        CreatedOnUtc = DateTime.UtcNow,
                    };
                    customer.Addresses.Add(shippingAddress);
                }

                customer.ShippingAddress = shippingAddress;
                _customerService.UpdateCustomer(customer);
            }

            bool isShippingSet = false;
            GetShippingOptionResponse getShippingOptionResponse = _shippingService.GetShippingOptions(cart, customer.ShippingAddress);

            if (checkoutDetails.UserSelectedOptions != null)
            {
                if (getShippingOptionResponse.Success && getShippingOptionResponse.ShippingOptions.Count > 0)
                {
                    foreach (var shippingOption in getShippingOptionResponse.ShippingOptions)
                    {
                        if (checkoutDetails.UserSelectedOptions.ShippingOptionName.Contains(shippingOption.Name) &&
                            checkoutDetails.UserSelectedOptions.ShippingOptionName.Contains(shippingOption.Description))
                        {
                            _genericAttributeService.SaveAttribute(Services.WorkContext.CurrentCustomer, SystemCustomerAttributeNames.SelectedShippingOption, shippingOption);
                            isShippingSet = true;
                            break;
                        }

                    }
                }

                if (!isShippingSet)
                {
                    var shippingOption = new ShippingOption();
                    shippingOption.Name = checkoutDetails.UserSelectedOptions.ShippingOptionName;
                    decimal shippingPrice = settings.DefaultShippingPrice;
                    decimal.TryParse(checkoutDetails.UserSelectedOptions.ShippingOptionAmount.Value, out shippingPrice);
                    shippingOption.Rate = shippingPrice;
                    _genericAttributeService.SaveAttribute(customer, SystemCustomerAttributeNames.SelectedShippingOption, shippingOption);
                }
            }

            processPaymentRequest.PaypalPayerId = checkoutDetails.PayerInfo.PayerID;


            return processPaymentRequest;
        }

        public DoExpressCheckoutPaymentResponseType DoExpressCheckoutPayment(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new DoExpressCheckoutPaymentResponseType();
			var store = Services.StoreService.GetStoreById(processPaymentRequest.StoreId);
			var settings = Services.Settings.LoadSetting<PayPalExpressPaymentSettings>(processPaymentRequest.StoreId);

            // populate payment details
            var paymentDetails = new PaymentDetailsType
            {
                OrderTotal = new BasicAmountType
                {
                    Value = Math.Round(processPaymentRequest.OrderTotal, 2).ToString("N", new CultureInfo("en-us")),
                    currencyID = GetApiCurrency(store.PrimaryStoreCurrency)
                },
                Custom = processPaymentRequest.OrderGuid.ToString(),
                ButtonSource = SmartStoreVersion.CurrentFullVersion
            };

            // build the request
            var req = new DoExpressCheckoutPaymentReq
            {
                DoExpressCheckoutPaymentRequest = new DoExpressCheckoutPaymentRequestType
                {
                    Version = ApiVersion,
                    DoExpressCheckoutPaymentRequestDetails = new DoExpressCheckoutPaymentRequestDetailsType
                    {
                        Token = processPaymentRequest.PaypalToken,
                        PayerID = processPaymentRequest.PaypalPayerId,
                        PaymentAction = GetPaymentAction(settings),
                        PaymentActionSpecified = true,
                        PaymentDetails = new PaymentDetailsType[]
                        {
                            paymentDetails
                        }
                    }
                }
            };

            //execute request
            using (var service = GetApiAaService(settings))
            {
                result = service.DoExpressCheckoutPayment(req);
            }
            return result;
        }
    }
}