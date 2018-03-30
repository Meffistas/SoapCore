using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace SoapCore
{
	public class SoapEndpointMiddleware
	{
		private readonly ILogger<SoapEndpointMiddleware> _logger;
		private readonly RequestDelegate _next;
		private readonly ServiceDescription _service;
		private readonly string _endpointPath;
		private readonly MessageEncoder _messageEncoder;
		private readonly SoapSerializer _serializer;

		public SoapEndpointMiddleware(ILogger<SoapEndpointMiddleware> logger, RequestDelegate next, Type serviceType, string path, MessageEncoder encoder, SoapSerializer serializer)
		{
			_logger = logger;
			_next = next;
			_endpointPath = path;
			_messageEncoder = encoder;
			_serializer = serializer;
			_service = new ServiceDescription(serviceType);
		}

		public async Task Invoke(HttpContext httpContext, IServiceProvider serviceProvider)
		{
			httpContext.Request.EnableRewind();
			if (httpContext.Request.Path.Equals(_endpointPath, StringComparison.Ordinal))
			{
				_logger.LogDebug($"Received SOAP Request for {httpContext.Request.Path} ({httpContext.Request.ContentLength ?? 0} bytes)");

				if (httpContext.Request.Query.ContainsKey("wsdl") && httpContext.Request.Method?.ToLower() == "get")
				{
					ProcessMeta(httpContext);
				}
				else
				{
					await ProcessOperation(httpContext, serviceProvider);
				}
			}
			else
			{
				await _next(httpContext);
			}
		}

		private Message ProcessMeta(HttpContext httpContext)
		{
			string baseUrl = httpContext.Request.Scheme + "://" + httpContext.Request.Host + httpContext.Request.PathBase + httpContext.Request.Path;

			var bodyWriter = new MetaBodyWriter(_service, baseUrl);

			var responseMessage = Message.CreateMessage(_messageEncoder.MessageVersion, null, bodyWriter);
			responseMessage = new MetaMessage(responseMessage, _service);

			httpContext.Response.ContentType = _messageEncoder.ContentType;
			_messageEncoder.WriteMessage(responseMessage, httpContext.Response.Body);

			return responseMessage;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		string GetSoapAction(HttpContext httpContext, Message requestMessage, System.Xml.XmlDictionaryReader reader)
		{
			var soapAction = httpContext.Request.Headers["SOAPAction"].FirstOrDefault();
			if (string.IsNullOrEmpty(soapAction))
			{
				foreach (var headerItem in httpContext.Request.Headers["Content-Type"])
				{
					// I want to avoid allocation as possible as I can(I hope to use Span<T> or Utf8String)
					// soap1.2: action name is in Content-Type(like 'action="[action url]"') or body
					int i = 0;
					// skip whitespace
					while (i < headerItem.Length && headerItem[i] == ' ')
					{
						i++;
					}
					if(headerItem.Length - i < 6)
					{
						continue;
					}
					// find 'action'
					if (headerItem[i + 0] == 'a'
						&& headerItem[i + 1] == 'c'
						&& headerItem[i + 2] == 't'
						&& headerItem[i + 3] == 'i'
						&& headerItem[i + 4] == 'o'
						&& headerItem[i + 5] == 'n')
					{
						i += 6;
						// skip white space
						while (i < headerItem.Length && headerItem[i] == ' ')
						{
							i++;
						}
						if (headerItem[i] == '=')
						{
							i++;
							// skip whitespace
							while (i < headerItem.Length && headerItem[i] == ' ')
							{
								i++;
							}
							// action value should be surrounded by '"'
							if (headerItem[i] == '"')
							{
								i++;
								int offset = i;
								while (i < headerItem.Length && headerItem[i] != '"')
								{
									i++;
								}
								if (i < headerItem.Length && headerItem[i] == '"')
								{
									var charray = headerItem.ToCharArray();
									soapAction = new string(charray, offset, i - offset);
									break;
								}
							}
						}
					}
				}
				if (string.IsNullOrEmpty(soapAction))
				{
					soapAction = reader.LocalName;
				}
			}
			if (!string.IsNullOrEmpty(soapAction))
			{
				// soapAction may have '"' in some cases.
				soapAction = soapAction.Trim('"');
			}
			return soapAction;
		}

		private async Task<Message> ProcessOperation(HttpContext httpContext, IServiceProvider serviceProvider)
		{
			Message responseMessage;

			//Reload the body to ensure we have the full message
			using (var reader = new StreamReader(httpContext.Request.Body))
			{
				var body = await reader.ReadToEndAsync();
				var requestData = Encoding.UTF8.GetBytes(body);
				httpContext.Request.Body = new MemoryStream(requestData);
			}

			//Return metadata if no request
			if (httpContext.Request.Body.Length == 0)
				return ProcessMeta(httpContext);

			//Get the message
			var requestMessage = _messageEncoder.ReadMessage(httpContext.Request.Body, 0x10000, httpContext.Request.ContentType);

			var messageInspector = serviceProvider.GetService<IMessageInspector>();
			messageInspector?.AfterReceiveRequest(requestMessage);

			// for getting soapaction and parameters in body
			// GetReaderAtBodyContents must not be called twice in one request
			using (var reader = requestMessage.GetReaderAtBodyContents())
			{
				var soapAction = GetSoapAction(httpContext, requestMessage, reader);
				requestMessage.Headers.Action = soapAction;
				var operation = _service.Operations.FirstOrDefault(o => o.SoapAction.Equals(soapAction, StringComparison.Ordinal) || o.Name.Equals(soapAction, StringComparison.Ordinal));
				if (operation == null)
				{
					throw new InvalidOperationException($"No operation found for specified action: {requestMessage.Headers.Action}");
				}
				_logger.LogInformation($"Request for operation {operation.Contract.Name}.{operation.Name} received");

				try
				{
					//Create an instance of the service class
					var serviceInstance = serviceProvider.GetService(_service.ServiceType);

					var headerProperty = _service.ServiceType.GetProperty("MessageHeaders");
					if (headerProperty != null && headerProperty.PropertyType == requestMessage.Headers.GetType())
					{
						headerProperty.SetValue(serviceInstance, requestMessage.Headers);
					}

					// Get operation arguments from message
					Dictionary<string, object> outArgs = new Dictionary<string, object>();
					var arguments = GetRequestArguments(requestMessage, reader, operation, ref outArgs);
					var allArgs = arguments.Concat(outArgs.Values).ToArray();

					// Invoke Operation method
					var responseObject = operation.DispatchMethod.Invoke(serviceInstance, allArgs);
					if (operation.DispatchMethod.ReturnType.IsConstructedGenericType && operation.DispatchMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
					{
						var responseTask = (Task)responseObject;
						await responseTask;
						responseObject = responseTask.GetType().GetProperty("Result").GetValue(responseTask);
					}
					int i = arguments.Length;
					var resultOutDictionary = new Dictionary<string, object>();
					foreach (var outArg in outArgs)
					{
						resultOutDictionary[outArg.Key] = allArgs[i];
						i++;
					}

					// Create response message
					var resultName = operation.DispatchMethod.ReturnParameter.GetCustomAttribute<MessageParameterAttribute>()?.Name ?? operation.Name + "Result";
					var bodyWriter = new ServiceBodyWriter(_serializer, operation.Contract.Namespace, operation.Name + "Response", resultName, responseObject, resultOutDictionary);
					responseMessage = Message.CreateMessage(_messageEncoder.MessageVersion, null, bodyWriter);
					responseMessage = new CustomMessage(responseMessage);

					httpContext.Response.ContentType = httpContext.Request.ContentType;
					httpContext.Response.Headers["SOAPAction"] = responseMessage.Headers.Action;

					messageInspector?.BeforeSendReply(responseMessage);

					_messageEncoder.WriteMessage(responseMessage, httpContext.Response.Body);
				}
				catch (Exception exception)
				{
					_logger.LogWarning(0, exception, exception.Message);
					responseMessage = WriteErrorResponseMessage(exception, StatusCodes.Status500InternalServerError, serviceProvider, httpContext);

				}
			}

			return responseMessage;
		}

		private object[] GetRequestArguments(Message requestMessage, System.Xml.XmlDictionaryReader xmlReader, OperationDescription operation, ref Dictionary<string, object> outArgs)
		{
			var parameters = operation.DispatchMethod.GetParameters().Where(x => !x.IsOut && !x.ParameterType.IsByRef).ToArray();
			var arguments = new List<object>();

			// Find the element for the operation's data
			xmlReader.ReadStartElement(operation.Name, operation.Contract.Namespace);

			for (int i = 0; i < parameters.Length; i++)
			{
				var elementAttribute = parameters[i].GetCustomAttribute<XmlElementAttribute>();
				var parameterName = !string.IsNullOrEmpty(elementAttribute?.ElementName)
										? elementAttribute.ElementName
										: parameters[i].GetCustomAttribute<MessageParameterAttribute>()?.Name ?? parameters[i].Name;
				var parameterNs = elementAttribute?.Namespace ?? operation.Contract.Namespace;

				if (xmlReader.IsStartElement(parameterName, parameterNs))
				{
					xmlReader.MoveToStartElement(parameterName, parameterNs);

					if (xmlReader.IsStartElement(parameterName, parameterNs))
					{
						var elementType = parameters[i].ParameterType.GetElementType();
						if (elementType == null || parameters[i].ParameterType.IsArray)
							elementType = parameters[i].ParameterType;

						switch (_serializer)
						{
							case SoapSerializer.XmlSerializer:
								{
									// see https://referencesource.microsoft.com/System.Xml/System/Xml/Serialization/XmlSerializer.cs.html#c97688a6c07294d5
									var serializer = new XmlSerializer(elementType, null, new Type[0], new XmlRootAttribute(parameterName), parameterNs);
									arguments.Add(serializer.Deserialize(xmlReader));
								}
								break;
							case SoapSerializer.DataContractSerializer:
								{
									var serializer = new DataContractSerializer(elementType, parameterName, parameterNs);
									arguments.Add(serializer.ReadObject(xmlReader, verifyObjectName: true));
								}
								break;
							default: throw new NotImplementedException();
						}
					}
				}
				else
				{
					arguments.Add(null);
				}
			}

			var outParams = operation.DispatchMethod.GetParameters().Where(x => x.IsOut || x.ParameterType.IsByRef).ToArray();
			foreach (var parameterInfo in outParams)
			{
				if (parameterInfo.ParameterType.Name == "Guid&")
					outArgs[parameterInfo.Name] = Guid.Empty;
				else if (parameterInfo.ParameterType.Name == "String&" || parameterInfo.ParameterType.GetElementType().IsArray)
					outArgs[parameterInfo.Name] = null;
				else
				{
					var type = parameterInfo.ParameterType.GetElementType();
					outArgs[parameterInfo.Name] = Activator.CreateInstance(type);
				}
			}
			return arguments.ToArray();
		}

		/// <summary>
		/// Helper message to write an error response message in case of an exception.
		/// </summary>
		/// <param name="exception">
		/// The exception that caused the failure.
		/// </param>
		/// <param name="statusCode">
		/// The HTTP status code that shall be returned to the caller.
		/// </param>
		/// <param name="serviceProvider">
		/// The DI container.
		/// </param>
		/// <param name="httpContext">
		/// The HTTP context that received the response message.
		/// </param>
		/// <returns>
		/// Returns the constructed message (which is implicitly written to the response
		/// and therefore must not be handled by the caller).
		/// </returns>
		private Message WriteErrorResponseMessage(
			Exception exception,
			int statusCode,
			IServiceProvider serviceProvider,
			HttpContext httpContext)
		{
			Message responseMessage;

			// Create response message

			string errorText = exception.InnerException != null ? exception.InnerException.Message : exception.Message; ;
			var transformer = serviceProvider.GetService<ExceptionTransformer>();
			if (transformer != null)
				errorText = transformer.Transform(exception);

			var bodyWriter = new FaultBodyWriter(new Fault { FaultString = errorText });
			responseMessage = Message.CreateMessage(_messageEncoder.MessageVersion, null, bodyWriter);
			responseMessage = new CustomMessage(responseMessage);

			httpContext.Response.ContentType = httpContext.Request.ContentType;
			httpContext.Response.Headers["SOAPAction"] = responseMessage.Headers.Action;
			httpContext.Response.StatusCode = statusCode;
			_messageEncoder.WriteMessage(responseMessage, httpContext.Response.Body);

			return responseMessage;
		}
	}
}
