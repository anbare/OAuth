﻿using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using BusinessService.Kyc;
using Common.Log;
using Common.PasswordTools;
using Core.Clients;
using Core.Email;
using Lykke.Service.Registration;
using Lykke.Service.Registration.Models;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WebAuth.ActionHandlers;
using WebAuth.Extensions;
using WebAuth.Managers;
using WebAuth.Models;

namespace WebAuth.Controllers
{
    public class AuthenticationController : BaseController
    {
        private readonly ILykkeRegistrationClient _registrationClient;
        private readonly IClientAccountsRepository _clientAccountsRepository;
        private readonly IVerificationCodesRepository _verificationCodesRepository;
        private readonly IEmailFacadeService _emailFacadeService;
        private readonly ProfileActionHandler _profileActionHandler;
        private readonly IUserManager _userManager;
        private readonly ILog _log;

        public AuthenticationController(
            ILykkeRegistrationClient registrationClient,
            IClientAccountsRepository clientAccountsRepository,
            IVerificationCodesRepository verificationCodesRepository,
            IEmailFacadeService emailFacadeService,
            ProfileActionHandler profileActionHandler,
            IUserManager userManager, 
            ILog log)
        {
            _registrationClient = registrationClient;
            _clientAccountsRepository = clientAccountsRepository;
            _verificationCodesRepository = verificationCodesRepository;
            _emailFacadeService = emailFacadeService;
            _profileActionHandler = profileActionHandler;
            _userManager = userManager;
            _log = log;
        }

        [HttpGet("~/signin")]
        [HttpGet("~/register")]
        public async Task<ActionResult> Login(string returnUrl = null)
        {
            try
            {
                string referer = this.GetReferer() ?? Request.GetUri().ToString();
                return View("Login", new LoginViewModel(returnUrl, referer));
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(ex.Source, "Signin", null, ex);
                return Content(ex.Message);
            }
        }

        [HttpPost("~/signin")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Signin(LoginViewModel model)
        {
            if (model.IsLogin)
            {
                if (!ModelState.IsValid)
                {
                    return View("Login", model);
                }

                AuthResponse authResult = await _registrationClient.AuthorizeAsync(new AuthModel
                {
                    Email = model.Username,
                    Password = model.Password,
                    Ip = this.GetIp(),
                    UserAgent = this.GetUserAgent()
                });

                if (authResult == null)
                {
                    ModelState.AddModelError("", "Technical problems during authorization.");
                    return View("Login", model);
                }

                if (authResult.Status == AuthenticationStatus.Error)
                {
                    ModelState.AddModelError("", "The username or password you entered is incorrect");
                    return View("Login", model);
                }

                var identity = await _userManager.CreateUserIdentityAsync(authResult.Account.Id,
                    authResult.Account.Email, model.Username, false);

                await HttpContext.Authentication.SignInAsync("ServerCookie", new ClaimsPrincipal(identity));

                return RedirectToLocal(model.ReturnUrl);
            }

            ModelState.ClearValidationState(nameof(model.Username));
            ModelState.ClearValidationState(nameof(model.Password));

            if (string.IsNullOrEmpty(model.Email))
            {
                ModelState.AddModelError(nameof(model.Email), $"{nameof(model.Email)} is required and can't be empty");
                
                return View("Login", model);
            }

            if (ModelState.GetFieldValidationState("Email") == ModelValidationState.Invalid)
            {
                return View("Login", model);
            }

            var code = await _verificationCodesRepository.AddCodeAsync(model.Email, model.Referer, model.ReturnUrl);
            var url = Url.Action("Signup", "Authentication", new {key = code.Key}, Request.Scheme);
            await _emailFacadeService.SendVerifyCode(model.Email, code.Code, url);

            return RedirectToAction("Signup", new {key = code.Key});
        }

        [HttpGet("~/signup/{key}")]
        public async Task<ActionResult> Signup(string key)
        {
            var code = await _verificationCodesRepository.GetCodeAsync(key);

            if (code == null)
                return RedirectToAction("Signin");

            return View(code);
        }

        [HttpPost("~/signup/verifyEmail")]  
        [ValidateAntiForgeryToken]
        public async Task<VerificationCodeResult> VerifyEmail([FromBody] VerificationCodeRequest request)
        {
            var result = new VerificationCodeResult();

            var existingCode = await _verificationCodesRepository.GetCodeAsync(request.Key);

            if (existingCode != null && existingCode.Code == request.Code)
            {
                result.Code = existingCode;
                result.IsEmailTaken = await _clientAccountsRepository.IsTraderWithEmailExistsAsync(existingCode.Email);

                if (result.IsEmailTaken)
                    await _verificationCodesRepository.DeleteCodesAsync(existingCode.Email);
            }

            return result;
        }

        [HttpPost("~/signup/resendCode")]  
        [ValidateAntiForgeryToken]
        public async Task ResendCode([FromBody]string key)
        {
            var code = await _verificationCodesRepository.GetCodeAsync(key);

            if (code != null && code.ResendCount < 2)
            {
                code = await _verificationCodesRepository.UpdateCodeAsync(key);
                var url = Url.Action("Signup", "Authentication", new { key = code.Key }, Request.Scheme);
                await _emailFacadeService.SendVerifyCode(code.Email, code.Code, url);
            }
        }

        [HttpPost("~/signup/complete")]
        [ValidateAntiForgeryToken]
        public async Task<RegistrationResultModel> CompleteRegistration([FromBody]SignUpViewModel model)
        {
            var regResult = new RegistrationResultModel();

            if (ModelState.IsValid)
            {
                string userIp = this.GetIp();
                string referer = null;
                string userAgent = this.GetUserAgent();

                if (!string.IsNullOrEmpty(model.Referer))
                    referer = new Uri(model.Referer).Host;

                RegistrationResponse result = await _registrationClient.RegisterAsync(new RegistrationModel
                {
                    Email = model.Email,
                    Password = PasswordKeepingUtils.GetClientHashedPwd(model.Password),
                    Ip = userIp,
                    Changer = RecordChanger.Client,
                    UserAgent = userAgent,
                    Referer = referer
                });

                regResult.RegistrationResponse = result;

                if (regResult.RegistrationResponse == null)
                {
                    regResult.Errors.Add("Technical problems during registration.");
                    return regResult;
                }

                var clientAccount = new Core.Clients.ClientAccount
                {
                    Id = result.Account.Id,
                    Email = result.Account.Email,
                    Registered = result.Account.Registered,
                    NotificationsId = result.Account.NotificationsId,
                    Phone = result.Account.Phone
                };

                var identity = await _userManager.CreateUserIdentityAsync(clientAccount.Id, clientAccount.Email, model.Email, true);

                await HttpContext.Authentication.SignInAsync("ServerCookie", new ClaimsPrincipal(identity));

                await _profileActionHandler.UpdatePersonalInformation(clientAccount.Id, model.FirstName, model.LastName);
                await _verificationCodesRepository.DeleteCodesAsync(model.Email);
            }
            else
            {
                var errors = ModelState.Values
                    .Where(item => item.ValidationState == ModelValidationState.Invalid)
                    .SelectMany(item => item.Errors);

                foreach (var error in errors)
                {
                    regResult.Errors.Add(error.ErrorMessage);
                }
            }

            return regResult;
        }

        [HttpGet("~/signout")]
        [HttpPost("~/signout")]
        public ActionResult SignOut()
        {
            return SignOut("ServerCookie");
        }
    }
}
