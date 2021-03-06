﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using System.Xml;
using System.Net.Sockets;
using System.Reflection;

namespace ru.org.openam.sdk.pll
{
    public enum type
    {
        unknown,
        auth,
        session,
        naming,
        identity,
        policy
    };

    public abstract class  Request
    {
		static Request(){
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
			ServicePointManager.DefaultConnectionLimit = 128;
            ServicePointManager.DnsRefreshTimeout = 60*1000;
            //ServicePointManager.EnableDnsRoundRobin = true;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.SetTcpKeepAlive(true,15*1000,5*1000);
            ServicePointManager.MaxServicePointIdleTime = 14 * 1000; //https://support.microsoft.com/ru-ru/help/2017977/the-underlying-connection-was-closed-a-connection-that-was-expected-to
            ServicePointManager.MaxServicePoints = 0;
            ServicePointManager.MaxServicePointIdleTime = 10*1000;
            if ("true".Equals(ConfigurationManager.AppSettings["com.sun.identity.agents.config.trust.server.certs"]))
				ServicePointManager.ServerCertificateValidationCallback +=
					delegate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate certificate,
						System.Security.Cryptography.X509Certificates.X509Chain chain,
						System.Net.Security.SslPolicyErrors sslPolicyErrors)
				{
					return true; // **** Always accept
				};
		}

        public type svcid=type.unknown;
        
		abstract override public String ToString();

		abstract public Uri getUrl();
		
		public  CookieContainer cookieContainer=null;
		virtual public CookieContainer getCookieContainer(){
			if (cookieContainer == null) {
				cookieContainer = new CookieContainer ();
				cookieContainer.Add(new Cookie("track", uuid.ToString()) { Domain = getUrl().Host });
			}
			return cookieContainer;
		}

		virtual protected naming.Response GetNaming(){
			return Bootstrap.GetNaming ();
		}

		static string UserAgent = string.Format(
            "OpenAM Policy Agent (.Net {0} {1} {2}/{3})"
				,Agent.getVersion()
				,Bootstrap.getAppUser()
				,System.Environment.MachineName
				,String.Join(",",((from ip in Dns.GetHostAddresses(System.Environment.MachineName) where ip.AddressFamily == AddressFamily.InterNetwork select ip.ToString()).ToList() )
			)
		);

        static bool? keepAlive = null;
        static bool KeepAlive(){
            if (keepAlive == null)
            {
                String setting = ConfigurationManager.AppSettings["org.openidentityplatform.agents.config.keepalive.disable"];
                if (String.IsNullOrEmpty(setting)&&Agent.Instance.HasConfig())
                    setting = Agent.Instance.GetSingle("org.openidentityplatform.agents.config.keepalive.disable");
                keepAlive = !(!String.IsNullOrEmpty(setting) && bool.Parse(setting));
            }
            return (bool)keepAlive; 
        }

		HttpWebRequest getHttpWebRequest()
		{
			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(getUrl());
            request.KeepAlive =KeepAlive();
            request.ProtocolVersion = (request.KeepAlive) ? HttpVersion.Version11 : HttpVersion.Version10;
            request.AutomaticDecompression = DecompressionMethods.None; //TODO configure
			request.Method = getMethod();
			request.ContentType = getContentType();
			request.UserAgent = UserAgent;
			request.CookieContainer = getCookieContainer();
            request.Proxy = null;
			int connect_timeout=5000,receive_timeout=15000;
			if (Agent.Instance.HasConfig()) {
				int.TryParse (Agent.Instance.GetSingle ("com.sun.identity.agents.config.connect.timeout"), out connect_timeout);
				int.TryParse (Agent.Instance.GetSingle ("com.sun.identity.agents.config.receive.timeout"), out receive_timeout);
			} else {
				int.TryParse (ConfigurationManager.AppSettings["com.sun.identity.agents.config.connect.timeout"], out connect_timeout);
				int.TryParse (ConfigurationManager.AppSettings["com.sun.identity.agents.config.receive.timeout"], out receive_timeout);
			}
			if (connect_timeout>0)
				request.Timeout = Math.Max(connect_timeout,3000);
			if (receive_timeout>0)
				request.ReadWriteTimeout = Math.Max(receive_timeout,5000);
			return request;
		}

		virtual protected String getMethod(){
			return "POST";
		}

		virtual protected String getContentType(){
			return "text/xml; encoding='utf-8'";
		}

		protected Guid uuid = Guid.NewGuid(); //tracking

		virtual protected String getRequestString(){
			return new pll.RequestSet(new pll.Request[]{this}).ToString();
		}

		virtual protected Response getResponse(String data){
			ResponseSet responses = new ResponseSet(getCookieContainer(),data);
			if (responses.Count > 0)
				return (Response)responses[0];
			throw new Exception(data);
		}

		public Response getResponse()
		{
			HttpWebRequest request = getHttpWebRequest();

			String body = getRequestString();
			byte[] postBytes = (new UTF8Encoding()).GetBytes(body);
			request.ContentLength = postBytes.Length;
			using (Stream requestStream = request.GetRequestStream()){ 
				try{
					requestStream.Write(postBytes, 0, postBytes.Length);
				}finally{
					requestStream.Close();
				}
			}
			try {
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse ()) {
					try {
						Log.Info (string.Format ("{1} {2}{0}{3}{4}{0}", Environment.NewLine, request.Method, request.RequestUri, request.Headers, body));
						String data = "";
                        using (Stream receiveStream = response.GetResponseStream())
                            try
                            {
                                using (StreamReader streamReader = new StreamReader(receiveStream, Encoding.UTF8))
                                {
                                    try
                                    {
                                        data = streamReader.ReadToEnd();
                                        Log.Info(string.Format("Message received (uuid: {1}){0}{3}{2}{0}", Environment.NewLine, uuid, data, response.Headers));
                                    }
                                    catch (XmlException e)
                                    {
                                        Log.Error(string.Format("Message received (uuid: {1}){0}{3}{2}{0}", Environment.NewLine, uuid, data, response.Headers));
                                        throw e;
                                    }
                                    finally
                                    {
                                        streamReader.Close();
                                    }
                                    return getResponse(data);
                                }
                            }finally{
                                receiveStream.Close();
                            }
					} finally {
						response.Close ();
					}
				}
			} catch (WebException e) {
				Log.Error (string.Format ("Message received (uuid: {1}){0}{2}{0}", Environment.NewLine, uuid, e));
				throw e;
			}
		}
    }
}
