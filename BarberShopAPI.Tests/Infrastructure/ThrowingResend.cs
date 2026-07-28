using Resend;
using System.Reflection;

namespace BarberShopAPI.Tests.Infrastructure
{
    /* An IResend whose every call fails, for driving the email jobs' catch blocks.
     *
     * Built with DispatchProxy rather than a hand-written class: IResend has ~80 members (audiences,
     * broadcasts, contacts, domains...) of which EmailService uses exactly one, so implementing it by hand
     * would be pages of NotImplementedException that break whenever the package adds a method.
     *
     * Throwing an HttpRequestException matches the real failure mode - Resend being unreachable or
     * returning an error - and EmailService catches plain Exception and puts ex.Message into ReviewReason. */
    public class ThrowingResend : DispatchProxy
    {
        /* This class's job is to emulate: "The email service provider is down and sending an email fails".
         * A Proxy is an object that pretends to implement an interface
         Imagine you have public interface IResend{
        Task SendEmailAsync(string email)
         }
        Normally you have IResend resend = new RealResend();
        The proxy lets you create IResend resend = new ThrowingResend();*/
        public const string FailureMessage = "Resend unavailable (test)";
        /* This is the error message my fake will produce */

        /* Returns a FAULTED TASK rather than throwing outright. Two reasons: throwing from a DispatchProxy
         * surfaces to the caller as TargetInvocationException wrapping the real one, and a real HTTP client
         * fails the task rather than throwing synchronously. Faulting the task means `await
         * _resend.EmailSendAsync(...)` raises the genuine HttpRequestException, exactly as it would live. */
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            /* This is called automatically whenever a method on the fake IResend is called.
             * Example: Your code: 
             await resend.EmailSendAsync(email);
            actually becomes:
            IResendProxy -> Invoke() -> Create failure
            So any call gets intercepted by Invoke()*/
        {
            var failure = new HttpRequestException(FailureMessage);
            var returnType = targetMethod?.ReturnType;

            if (returnType == typeof(Task))
                return Task.FromException(failure);

            if (returnType is { IsGenericType: true } && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var fromException = typeof(Task)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(m => m.Name == nameof(Task.FromException) && m.IsGenericMethod
                                 && m.GetParameters().Length == 1)
                    .MakeGenericMethod(returnType.GetGenericArguments()[0]);
                return fromException.Invoke(null, new object[] { failure });
            }

            throw failure;
        }

        public static IResend Create() => DispatchProxy.Create<IResend, ThrowingResend>();
    }
}
