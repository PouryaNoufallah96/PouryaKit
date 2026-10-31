using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;

namespace PouryaKit.Grpc;

/// <summary>Registers a gRPC client of another Kit Host that carries the Operation Context.</summary>
public static class GrpcClientServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the generated gRPC client <typeparamref name="TClient"/> under <paramref name="name"/>, calling the
        /// <see cref="PouryaKitGrpcClientOptions.Address"/> of the options named <paramref name="name"/>, which the Host
        /// binds from configuration and the Kit validates at start. Every call carries the Operation Context it is given
        /// with <c>WithOperationContext</c> as Web's Kit headers in its metadata, written by the client's interceptor,
        /// and the options' <see cref="PouryaKitGrpcClientOptions.Deadline"/> when it sets none of its own; a call
        /// without a context throws before it is sent. Nothing of an incoming request is copied, its bearer token least
        /// of all: a client that must authenticate to the peer adds its own credentials on the builder it returns. The
        /// Host's start fails when the address is one of the Host's own listening URLs, configured or bound by Kestrel:
        /// a call between Modules of one Host goes through the Gateway, never a loopback. Called again with the same name and client, it adds nothing
        /// and returns the builder of the client already registered, so the client's interceptor runs once; with the
        /// same name and another client, gRPC's client factory throws.
        /// </summary>
        public IHttpClientBuilder AddPouryaKitGrpcClient<TClient>(string name)
            where TClient : class
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            if (
                services.Any(descriptor =>
                    descriptor.ImplementationInstance is GrpcClient(var registered, var client)
                    && registered == name
                    && client == typeof(TClient)
                )
            )
            {
                return new ClientBuilder(name, services);
            }

            services.AddOptions<PouryaKitGrpcClientOptions>(name).ValidateOnStart();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IValidateOptions<PouryaKitGrpcClientOptions>,
                    ClientOptionsValidation
                >()
            );
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IStartupCheck, GrpcLoopbackCheck>()
            );
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, GrpcLoopbackCheck>()
            );
            services.AddSingleton(new GrpcClient(name, typeof(TClient)));
            return services
                .AddGrpcClient<TClient>(
                    name,
                    (provider, client) =>
                        client.Address = provider
                            .GetRequiredService<IOptionsMonitor<PouryaKitGrpcClientOptions>>()
                            .Get(name)
                            .Address
                )
                .AddInterceptor(provider => new OperationContextInterceptor(
                    provider
                        .GetRequiredService<IOptionsMonitor<PouryaKitGrpcClientOptions>>()
                        .Get(name)
                        .Deadline
                ));
        }
    }

    // The builder of a client already registered; the factory's own AddHttpClient would register its services again.
    private sealed record ClientBuilder(string Name, IServiceCollection Services)
        : IHttpClientBuilder;

    // The message names the client, so it is a validator of its own rather than a chained Validate.
    private sealed class ClientOptionsValidation : IValidateOptions<PouryaKitGrpcClientOptions>
    {
        public ValidateOptionsResult Validate(string? name, PouryaKitGrpcClientOptions options)
        {
            List<string> failures = [];
            if (options.Address is not { IsAbsoluteUri: true, Scheme: "http" or "https" })
            {
                failures.Add(
                    $"PouryaKitGrpcClientOptions.Address of the gRPC client {name} is not an absolute http or https address."
                );
            }

            if (options.Deadline <= TimeSpan.Zero)
            {
                failures.Add(
                    $"PouryaKitGrpcClientOptions.Deadline of the gRPC client {name} is not positive."
                );
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }
}

/// <summary>A registered gRPC client, by its name and generated client type.</summary>
internal sealed record GrpcClient(string Name, Type Client);
