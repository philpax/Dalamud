using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;

using Dalamud.Logging.Internal;

namespace Dalamud.IoC.Internal
{
    /// <summary>
    /// A simple singleton-only IOC container that provides (optional) version-based dependency resolution.
    /// </summary>
    internal class ServiceContainer : IServiceProvider, IServiceType
    {
        private static readonly ModuleLog Log = new("SERVICECONTAINER");

        private readonly Dictionary<Type, ObjectInstance> instances = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceContainer"/> class.
        /// </summary>
        public ServiceContainer()
        {
        }

        /// <summary>
        /// Register a singleton object of any type into the current IOC container.
        /// </summary>
        /// <param name="instance">The existing instance to register in the container.</param>
        /// <typeparam name="T">The interface to register.</typeparam>
        public void RegisterSingleton<T>(Task<T> instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            this.instances[typeof(T)] = new(instance.ContinueWith(x => new WeakReference(x.Result)), typeof(T));
        }

        /// <summary>
        /// Create an object.
        /// </summary>
        /// <param name="objectType">The type of object to create.</param>
        /// <param name="scopedObjects">Scoped objects to be included in the constructor.</param>
        /// <returns>The created object.</returns>
        public async Task<object?> CreateAsync(Type objectType, params object[] scopedObjects)
        {
            var ctor = this.FindApplicableCtor(objectType, scopedObjects);
            if (ctor == null)
            {
                Log.Error("Failed to create {TypeName}, an eligible ctor with satisfiable services could not be found", objectType.FullName!);
                return null;
            }

            // validate dependency versions (if they exist)
            var parameters = ctor.GetParameters().Select(p =>
            {
                var parameterType = p.ParameterType;
                var requiredVersion = p.GetCustomAttribute(typeof(RequiredVersionAttribute)) as RequiredVersionAttribute;
                return (parameterType, requiredVersion);
            }).ToList();

            var versionCheck = parameters.All(p => CheckInterfaceVersion(p.requiredVersion, p.parameterType));

            if (!versionCheck)
            {
                Log.Error("Failed to create {TypeName}, a RequestedVersion could not be satisfied", objectType.FullName!);
                return null;
            }

            var resolvedParams =
                await Task.WhenAll(
                    parameters
                        .Select(async p =>
                        {
                            var service = await this.GetService(p.parameterType, scopedObjects);

                            if (service == null)
                            {
                                Log.Error("Requested service type {TypeName} was not available (null)", p.parameterType.FullName!);
                            }

                            return service;
                        }));

            var hasNull = resolvedParams.Any(p => p == null);
            if (hasNull)
            {
                Log.Error("Failed to create {TypeName}, a requested service type could not be satisfied", objectType.FullName!);
                return null;
            }

            var instance = FormatterServices.GetUninitializedObject(objectType);

            if (!await this.InjectProperties(instance, scopedObjects))
            {
                Log.Error("Failed to create {TypeName}, a requested property service type could not be satisfied", objectType.FullName!);
                return null;
            }

            ctor.Invoke(instance, resolvedParams);

            return instance;
        }

        /// <summary>
        /// Inject <see cref="PluginInterfaceAttribute"/> interfaces into public or static properties on the provided object.
        /// The properties have to be marked with the <see cref="PluginServiceAttribute"/>.
        /// The properties can be marked with the <see cref="RequiredVersionAttribute"/> to lock down versions.
        /// </summary>
        /// <param name="instance">The object instance.</param>
        /// <param name="scopedObjects">Scoped objects.</param>
        /// <returns>Whether or not the injection was successful.</returns>
        public async Task<bool> InjectProperties(object instance, params object[] scopedObjects)
        {
            var objectType = instance.GetType();

            var props = objectType.GetProperties(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                                 BindingFlags.NonPublic).Where(x => x.GetCustomAttributes(typeof(PluginServiceAttribute)).Any()).Select(
                propertyInfo =>
                {
                    var requiredVersion = propertyInfo.GetCustomAttribute(typeof(RequiredVersionAttribute)) as RequiredVersionAttribute;
                    return (propertyInfo, requiredVersion);
                }).ToArray();

            var versionCheck = props.All(x => CheckInterfaceVersion(x.requiredVersion, x.propertyInfo.PropertyType));

            if (!versionCheck)
            {
                Log.Error("Failed to create {TypeName}, a RequestedVersion could not be satisfied", objectType.FullName!);
                return false;
            }

            foreach (var prop in props)
            {
                var service = await this.GetService(prop.propertyInfo.PropertyType, scopedObjects);

                if (service == null)
                {
                    Log.Error("Requested service type {TypeName} was not available (null)", prop.propertyInfo.PropertyType.FullName!);
                    return false;
                }

                prop.propertyInfo.SetValue(instance, service);
            }

            return true;
        }

        /// <inheritdoc/>
        object? IServiceProvider.GetService(Type serviceType) => this.GetService(serviceType);

        private static bool CheckInterfaceVersion(RequiredVersionAttribute? requiredVersion, Type parameterType)
        {
            // if there's no required version, ignore it
            if (requiredVersion == null)
                return true;

            // if there's no requested version, ignore it
            var declVersion = parameterType.GetCustomAttribute<InterfaceVersionAttribute>();
            if (declVersion == null)
                return true;

            if (declVersion.Version == requiredVersion.Version)
                return true;

            Log.Error(
                "Requested version {ReqVersion} does not match the implemented version {ImplVersion} for param type {ParamType}",
                requiredVersion.Version,
                declVersion.Version,
                parameterType.FullName!);

            return false;
        }

        private async Task<object?> GetService(Type serviceType, object[] scopedObjects)
        {
            var singletonService = await this.GetService(serviceType);
            if (singletonService != null)
            {
                return singletonService;
            }

            // resolve dependency from scoped objects
            var scoped = scopedObjects.FirstOrDefault(o => o.GetType() == serviceType);
            if (scoped == default)
            {
                return null;
            }

            return scoped;
        }

        private async Task<object?> GetService(Type serviceType)
        {
            if (!this.instances.TryGetValue(serviceType, out var service))
                return null;

            var instance = await service.InstanceTask;
            return instance.Target;
        }

        private ConstructorInfo? FindApplicableCtor(Type type, object[] scopedObjects)
        {
            // get a list of all the available types: scoped and singleton
            var types = scopedObjects
                .Select(o => o.GetType())
                .Union(this.instances.Keys)
                .ToArray();

            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            foreach (var ctor in ctors)
            {
                if (this.ValidateCtor(ctor, types))
                {
                    return ctor;
                }
            }

            return null;
        }

        private bool ValidateCtor(ConstructorInfo ctor, Type[] types)
        {
            var parameters = ctor.GetParameters();
            foreach (var parameter in parameters)
            {
                var contains = types.Contains(parameter.ParameterType);
                if (!contains)
                {
                    Log.Error("Failed to validate {TypeName}, unable to find any services that satisfy the type", parameter.ParameterType.FullName!);
                    return false;
                }
            }

            return true;
        }
    }
}
