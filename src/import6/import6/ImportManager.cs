﻿namespace import6
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Management;
    using System.Text;

    public class ImportManager
    {
        private DataAccess data;
        private string _host;
        private string _plan;
        private string _port;
        private string _key;

        public ImportManager(string hostName, string plan, string port, string key)
        {
            data = new DataAccess();
            this._host = hostName;
            this._plan = plan;
            this._port = port;
            this._key = key;
        }

        public ImportManager()
        {
            data = new DataAccess();
        }

        public List<Domain> GetAllDomains(string where = "")
        {
            var tmp = new List<Domain>();

            var _query = "SELECT * FROM IIsWebServerSetting";

            if (!String.IsNullOrEmpty(where))
                _query += " WHERE " + where;

            using (var query = data.GetProperties(_query))
            {
                foreach (ManagementObject item in query)
                {
                    String domainPath = String.Empty;
                    CustomError[] customErrors;
                    CustomHeader[] customHeaders;
                    MimeType[] customMimes;

                    var d = new Domain();
                    d.Name = data.GetValue<string>(item, "ServerComment");
                    d.MetaName = data.GetValue<string>(item, "Name");
                    
                    GetDomainPath(d.MetaName, out domainPath, out customErrors, out customHeaders, out customMimes);

                    d.HttpErrors = customErrors;
                    d.MimeTypes = customMimes;
                    d.Headers = customHeaders;
                    d.Path = domainPath;
                    d.EnableDirBrowsing = data.GetValue<bool>(item, "EnableDirBrowsing");
                    d.EnableSSL = isSSLEnabled(item);
                    d.Bindings = GetDomainBindings(item);

                    tmp.Add(d);
                }
            }

            return tmp;
        }

        public string GenerateAppCmdScripts(string where = "", 
                                                            bool dumpWebsite=false, 
                                                            bool dumpDirBrowsing=false, 
                                                            bool dumpHeaders=false, 
                                                            bool dumpHttpErrors=false, 
                                                            bool dumpMimetypes=false)
        {
            var sb = new StringBuilder();
            var list = GetAllDomains(where);

            foreach (var item in list)
            {

                if (dumpWebsite)
                    sb.AppendFormat("curl -d \"name={0}&planAlias={4}\" http://{1}:{2}/Api/v1/Domain/Create?key={3}", 
                                                                        item.Name, _host, _port, _key, _plan).AppendLine();

                if(dumpDirBrowsing)
                    if (item.EnableDirBrowsing)
                        sb.AppendFormat("appcmd.exe set config \"{0}\" -section:system.webServer/directoryBrowse /enabled:\"True\"", 
                                                                                                                    item.Name).AppendLine();

                if(dumpHeaders)
                    foreach (var header in item.Headers)                
                        sb.AppendFormat("appcmd.exe set config \"{0}\" -section:system.webServer/httpProtocol /+\"customHeaders.[name='{1}',value='{2}']\"", 
                                                                                                                item.Name, header.Name, header.Value).AppendLine();
                if(dumpHttpErrors)
                    foreach (var error in item.HttpErrors)                
                        sb.AppendFormat("appcmd.exe set config \"{0}\" -section:system.webServer/httpErrors /[statusCode='{1}',subStatusCode='-1'].prefixLanguageFilePath:\"{2}\" /[statusCode='{1}',subStatusCode='-1'].responseMode:\"{3}\""
                                                                                    ,item.Name, error.HttpErrorCode, error.HandlerLocation, error.HandlerType).AppendLine();
                if(dumpMimetypes)
                    foreach (var mime in item.MimeTypes)                
                        sb.AppendFormat("appcmd.exe set config \"{0}\" -section:system.webServer/staticContent /+\"[fileExtension='{1}',mimeType='{0}']\" ",
                                                                                                            item.Name, mime.Extension, mime.MType).AppendLine();                
            }

            return sb.ToString();
        }

        public string GenerateRobocopyScript(string destinationPath, string sourcePath = "", string where = "")
        {               
            var sb = new StringBuilder();            
            var list = GetAllDomains(where);

            sb.AppendLine("@echo off");

            foreach (var item in list)
            {
                var newpath = destinationPath.Replace("{DOMAIN}", item.Name);
                var newSourcePath = String.IsNullOrEmpty(sourcePath) ? item.Path : sourcePath.Replace("{DOMAIN}", item.Name);

                sb.AppendFormat("robocopy \"{0}\" \"{1}\" /Z /ETA /MIR", newSourcePath, newpath)
                    .AppendLine();
            }

            return sb.ToString();
        }

        public MimeType[] GetAllMimeTypes()
        {
            var list = new List<MimeType>();

            var _query = "SELECT * FROM IIsMimeMapSetting";

            using (var query = data.GetProperties(_query))
            {
                foreach (ManagementObject item in query)
                {
                    var currentMimes = GetMimeTypes(item);
                    list.AddRange(currentMimes);
                }
            }

            return list.ToArray();
        }

        public string GenerateAddDomainAliasAPIRequests(string where = "")
        {
            var sb = new StringBuilder();
            var list = GetAllDomains(where);

            foreach (var item in list)
            {
                if (item.Bindings.Any())
                {
                    foreach (var bind in item.Bindings)
                    {
                        if (bind.Hostname.IndexOf(item.Name) == -1)
                        {
                            if(!bind.Hostname.StartsWith("www"))
                                sb.AppendFormat("curl -d \"name={0}&alias={4}\" http://{1}:{2}/Api/v1/Domain/AddDomainAlias?key={3}",
                                                        item.Name, _host, _port, _key, bind.Hostname).AppendLine();
                        }
                    }
                }
            }

            return sb.ToString();
        }

        private void GetDomainPath(string metaName, out string domainPath, out CustomError[] errors, out CustomHeader[] headers, out MimeType[] mimes)
        {
            mimes = new List<MimeType>().ToArray();
            headers = new List<CustomHeader>().ToArray();
            errors = new List<CustomError>().ToArray();
            domainPath = String.Empty;
            metaName = String.Format("{0}/root", metaName);
            
            var _query = String.Format("SELECT * FROM IIsWebVirtualDirSetting WHERE Name = '{0}'", metaName);

            using (var query = data.GetProperties(_query))
            {                
                foreach (ManagementObject item in query)
                {                    
                    domainPath = data.GetValue<string>(item, "Path");
                    errors = GetErrorPages(item);
                    headers = GetCustomHeaders(item);
                    mimes = GetMimeTypes(item);

                    break;
                }
            }            
        }

        private Binding[] GetDomainBindings(ManagementObject item)
        {
            var currentBinding = new List<Binding>();

            if (item == null)
                return currentBinding.ToArray();

            var bindins = data.GetValue<ManagementBaseObject[]>(item, "ServerBindings");
           
            if (bindins == null)
                return currentBinding.ToArray();

            foreach (ManagementBaseObject bind in bindins)
            {
                var b = new Binding();
                b.Hostname = data.GetValue<string>(bind, "Hostname");
                b.Port = data.GetValue<string>(bind, "Port");

                currentBinding.Add(b);                
            }

            return currentBinding.ToArray();
        }

        private bool isSSLEnabled(ManagementObject item)
        {
            var bindins = data.GetValue<ManagementBaseObject[]>(item, "SecureBindings");
            var result = false;

            if (bindins == null)
                return result;

            foreach (ManagementBaseObject bind in bindins)
            {
                var port = bind.GetPropertyValue("Port");
                if (port != null)
                {
                    if (port.ToString() == "443")
                    {
                        result = true;
                        break;
                    }
                }
            }

            return result;
        }

        private CustomHeader[] GetCustomHeaders(ManagementObject item)
        {
            var list = new List<CustomHeader>();

            if (item == null)
                return list.ToArray();

            var headers = data.GetValue<ManagementBaseObject[]>(item, "HttpCustomHeaders");

            if (headers == null)
                return list.ToArray();            

            foreach (ManagementBaseObject header in headers)
            {
                if (header != null)
                {                    
                    var ch = new CustomHeader();                    
                    var pname = header.GetPropertyValue("Keyname");

                    if (pname != null)
                    {
                        ch.Name = pname.ToString().Split(':').FirstOrDefault();
                        ch.Value = pname.ToString().Split(':').LastOrDefault();

                        if (!String.IsNullOrEmpty(ch.Name))
                            ch.Name = ch.Name.Trim();

                        if (!String.IsNullOrEmpty(ch.Value))
                            ch.Value = ch.Value.Trim();

                        if(ch.Value != "ASP.NET")
                            list.Add(ch);
                    }
                }
            }

            return list.ToArray();
        }

        private CustomError[] GetErrorPages(ManagementObject item)
        {
            var list = new List<CustomError>();

            if (item == null)
                return list.ToArray();

            var errors = data.GetValue<ManagementBaseObject[]>(item, "HttpErrors");

            if (errors == null)
                return list.ToArray();

            if(!errors.Any())
                return list.ToArray();

            if(errors.Count() < 3)
                return list.ToArray();

            foreach (ManagementBaseObject error in errors)
            {
                var err = new CustomError();
                err.HandlerLocation = error.GetPropertyValue("HandlerLocation").ToString();

                //for IIS 7+: Redirect, File, ExecuteURL
                //for IIS 6: File, URL
                err.HandlerType = error.GetPropertyValue("HandlerType").ToString();                
                err.HttpErrorCode = error.GetPropertyValue("HttpErrorCode").ToString();
                err.HttpErrorSubcode = error.GetPropertyValue("HttpErrorSubcode").ToString();

                if (err.HandlerType == "URL")
                {                    
                    err.HandlerType = "ExecuteURL";
                    list.Add(err);
                }
            }

            return list.ToArray();
        }

        private MimeType[] GetMimeTypes(ManagementObject item)
        {           
            var list = new List<MimeType>();


            if (item == null)
                return list.ToArray();

            var mimes = data.GetValue<ManagementBaseObject[]>(item, "MimeMap");

            if (mimes == null)
                return list.ToArray();

            var defaults = GetDefaultMimeTypes();

            foreach (ManagementBaseObject mtype in mimes)
            {
                var mime = new MimeType();
                var mExtension = mtype.GetPropertyValue("Extension");
                var mMimeType = mtype.GetPropertyValue("MimeType");

                mime.Extension = mExtension != null ? mExtension.ToString() : "";
                mime.MType = mMimeType != null ? mMimeType.ToString() : "";
                
                if(!defaults.Contains(mime.Extension))
                    list.Add(mime);
            }

            return list.ToArray();
        }

        private List<string> GetDefaultMimeTypes()
        {
            var list = new List<string>();
            list.Add(".323");
            list.Add(".3g2");
            list.Add(".3gp2");
            list.Add(".3gp");
            list.Add(".3gpp");
            list.Add(".aaf");
            list.Add(".aac");
            list.Add(".aca");
            list.Add(".accdb");
            list.Add(".accde");
            list.Add(".accdt");
            list.Add(".acx");
            list.Add(".adt");
            list.Add(".adts");
            list.Add(".afm");
            list.Add(".ai");
            list.Add(".aif");
            list.Add(".aifc");
            list.Add(".aiff");
            list.Add(".applic");
            list.Add(".art");
            list.Add(".asd");
            list.Add(".asf");
            list.Add(".asi");
            list.Add(".asm");
            list.Add(".asr");
            list.Add(".asx");
            list.Add(".atom");
            list.Add(".au"); 
            list.Add(".avi");
            list.Add(".axs");
            list.Add(".bas");
            list.Add(".bcpio");
            list.Add(".bin");
            list.Add(".bmp");
            list.Add(".c"); 
            list.Add(".cab");
            list.Add(".calx");
            list.Add(".cat");
            list.Add(".cdf");
            list.Add(".chm");
            list.Add(".class");
            list.Add(".clp"); 
            list.Add(".cmx"); 
            list.Add(".cnf"); 
            list.Add(".cod"); 
            list.Add(".cpi");
            list.Add(".cpp"); 
            list.Add(".crd"); 
            list.Add(".crl"); 
            list.Add(".crt"); 
            list.Add(".csh"); 
            list.Add(".css"); 
            list.Add(".csv"); 
            list.Add(".cur"); 
            list.Add(".dcr"); 
            list.Add(".deploy");
            list.Add(".der"); 
            list.Add(".dib"); 
            list.Add(".dir"); 
            list.Add(".disco");
            list.Add(".dll"); 
            list.Add(".dll.config");
            list.Add(".dlm"); 
            list.Add(".doc"); 
            list.Add(".docm");
            list.Add(".docx");
            list.Add(".dot");
            list.Add(".dotm"); 
            list.Add(".dotx"); 
            list.Add(".dsp"); 
            list.Add(".dtd"); 
            list.Add(".dvi"); 
            list.Add(".dvr-ms");
            list.Add(".dwf"); 
            list.Add(".dwp"); 
            list.Add(".dxr"); 
            list.Add(".eml"); 
            list.Add(".emz"); 
            list.Add(".eot"); 
            list.Add(".eps"); 
            list.Add(".etx"); 
            list.Add(".evy"); 
            list.Add(".exe"); 
            list.Add(".exe.config");
            list.Add(".fdf"); 
            list.Add(".fif"); 
            list.Add(".fla"); 
            list.Add(".flr"); 
            list.Add(".flv"); 
            list.Add(".gif"); 
            list.Add(".gtar"); 
            list.Add(".gz");
            list.Add(".h");
            list.Add(".hdf");
            list.Add(".hdml"); 
            list.Add(".hhc"); 
            list.Add(".hhk"); 
            list.Add(".hhp"); 
            list.Add(".hlp"); 
            list.Add(".hqx"); 
            list.Add(".hta"); 
            list.Add(".htc"); 
            list.Add(".htm"); 
            list.Add(".html");
            list.Add(".htt"); 
            list.Add(".hxt"); 
            list.Add(".ico"); 
            list.Add(".ics"); 
            list.Add(".ief"); 
            list.Add(".iii"); 
            list.Add(".inf"); 
            list.Add(".ins"); 
            list.Add(".isp"); 
            list.Add(".IVF"); 
            list.Add(".jar"); 
            list.Add(".java");
            list.Add(".jck"); 
            list.Add(".jcz"); 
            list.Add(".jfif"); 
            list.Add(".jpb"); 
            list.Add(".jpe"); 
            list.Add(".jpeg"); 
            list.Add(".jpg"); 
            list.Add(".js"); 
            list.Add(".json"); 
            list.Add(".jsx"); 
            list.Add(".latex"); 
            list.Add(".lit"); 
            list.Add(".lpk"); 
            list.Add(".lsf"); 
            list.Add(".lsx"); 
            list.Add(".lzh"); 
            list.Add(".m13v"); 
            list.Add(".m14"); 
            list.Add(".m1v"); 
            list.Add(".m2ts"); 
            list.Add(".m3u");
            list.Add(".m4a"); 
            list.Add(".m4v"); 
            list.Add(".man"); 
            list.Add(".manifest");
            list.Add(".map"); 
            list.Add(".mdb"); 
            list.Add(".mdp"); 
            list.Add(".me"); 
            list.Add(".mht"); 
            list.Add(".mhtml"); 
            list.Add(".mid"); 
            list.Add(".midi"); 
            list.Add(".mix"); 
            list.Add(".mmf"); 
            list.Add(".mno"); 
            list.Add(".mnyv"); 
            list.Add(".mov"); 
            list.Add(".movie"); 
            list.Add(".mp2"); 
            list.Add(".mp3"); 
            list.Add(".mp4"); 
            list.Add(".mp4v"); 
            list.Add(".mpa"); 
            list.Add(".mpe"); 
            list.Add(".mpeg"); 
            list.Add(".mpgv");
            list.Add(".mpp"); 
            list.Add(".mpv2"); 
            list.Add(".ms"); 
            list.Add(".msi"); 
            list.Add(".mso"); 
            list.Add(".mvb"); 
            list.Add(".mvc"); 
            list.Add(".nc"); 
            list.Add(".nscv");
            list.Add(".nws"); 
            list.Add(".ocx"); 
            list.Add(".oda"); 
            list.Add(".odc"); 
            list.Add(".ods"); 
            list.Add(".oga"); 
            list.Add(".ogg"); 
            list.Add(".ogv"); 
            list.Add(".one"); 
            list.Add(".onea"); 
            list.Add(".onetoc"); 
            list.Add(".onetoc2"); 
            list.Add(".onetmp"); 
            list.Add(".onepkg"); 
            list.Add(".osdx"); 
            list.Add(".otf"); 
            list.Add(".p10"); 
            list.Add(".p12"); 
            list.Add(".p7b"); 
            list.Add(".p7c"); 
            list.Add(".p7m"); 
            list.Add(".p7r"); 
            list.Add(".p7s"); 
            list.Add(".pbm"); 
            list.Add(".pcx"); 
            list.Add(".pcz"); 
            list.Add(".pdf"); 
            list.Add(".pfb"); 
            list.Add(".pfm"); 
            list.Add(".pfx"); 
            list.Add(".pgm"); 
            list.Add(".pko"); 
            list.Add(".pma"); 
            list.Add(".pmc"); 
            list.Add(".pml"); 
            list.Add(".pmr"); 
            list.Add(".pmw"); 
            list.Add(".png"); 
            list.Add(".pnm"); 
            list.Add(".pnz"); 
            list.Add(".pot"); 
            list.Add(".potm");
            list.Add(".potx");
            list.Add(".ppam");
            list.Add(".ppm"); 
            list.Add(".pps"); 
            list.Add(".ppsm"); 
            list.Add(".ppsx"); 
            list.Add(".ppt"); 
            list.Add(".pptm"); 
            list.Add(".pptx"); 
            list.Add(".prf"); 
            list.Add(".prm"); 
            list.Add(".prx"); 
            list.Add(".ps"); 
            list.Add(".psd"); 
            list.Add(".psm"); 
            list.Add(".psp"); 
            list.Add(".pub"); 
            list.Add(".qt"); 
            list.Add(".qtl"); 
            list.Add(".qxd"); 
            list.Add(".ra"); 
            list.Add(".ram"); 
            list.Add(".rar"); 
            list.Add(".ras"); 
            list.Add(".rf"); 
            list.Add(".rgb"); 
            list.Add(".rm"); 
            list.Add(".rmi"); 
            list.Add(".roff"); 
            list.Add(".rpm"); 
            list.Add(".rtf"); 
            list.Add(".rtx"); 
            list.Add(".scd"); 
            list.Add(".sct"); 
            list.Add(".sea"); 
            list.Add(".setpay");
            list.Add(".setreg"); 
            list.Add(".sgml"); 
            list.Add(".sh"); 
            list.Add(".shar"); 
            list.Add(".sit"); 
            list.Add(".sldm"); 
            list.Add(".sldx"); 
            list.Add(".smd"); 
            list.Add(".smi"); 
            list.Add(".smx"); 
            list.Add(".smz"); 
            list.Add(".snd"); 
            list.Add(".snp"); 
            list.Add(".spc"); 
            list.Add(".spl"); 
            list.Add(".spx"); 
            list.Add(".src"); 
            list.Add(".ssm"); 
            list.Add(".sst"); 
            list.Add(".stl"); 
            list.Add(".sv4cpio"); 
            list.Add(".sv4crc"); 
            list.Add(".svg"); 
            list.Add(".svgz"); 
            list.Add(".swf"); 
            list.Add(".t"); 
            list.Add(".tar"); 
            list.Add(".tcl"); 
            list.Add(".tex"); 
            list.Add(".texi"); 
            list.Add(".texinfo"); 
            list.Add(".tgz"); 
            list.Add(".thmx"); 
            list.Add(".thn"); 
            list.Add(".tif"); 
            list.Add(".tiff"); 
            list.Add(".toc"); 
            list.Add(".tr"); 
            list.Add(".trm"); 
            list.Add(".ts"); 
            list.Add(".tsv"); 
            list.Add(".ttf"); 
            list.Add(".tts"); 
            list.Add(".txt"); 
            list.Add(".u32"); 
            list.Add(".uls"); 
            list.Add(".ustar"); 
            list.Add(".vbs"); 
            list.Add(".vcf"); 
            list.Add(".vcs"); 
            list.Add(".vdx"); 
            list.Add(".vml"); 
            list.Add(".vsd"); 
            list.Add(".vss"); 
            list.Add(".vst"); 
            list.Add(".vsto"); 
            list.Add(".vsw"); 
            list.Add(".vsx"); 
            list.Add(".vtx"); 
            list.Add(".wav"); 
            list.Add(".wax"); 
            list.Add(".wbmp"); 
            list.Add(".wcm"); 
            list.Add(".wdb"); 
            list.Add(".webm"); 
            list.Add(".wks"); 
            list.Add(".wm"); 
            list.Add(".wma"); 
            list.Add(".wmd"); 
            list.Add(".wmf"); 
            list.Add(".wml"); 
            list.Add(".wmlc"); 
            list.Add(".wmls"); 
            list.Add(".wmlsc"); 
            list.Add(".wmp");
            list.Add(".wmv"); 
            list.Add(".wmx"); 
            list.Add(".wmz"); 
            list.Add(".woff"); 
            list.Add(".wps"); 
            list.Add(".wri"); 
            list.Add(".wrl"); 
            list.Add(".wrz"); 
            list.Add(".wsdl"); 
            list.Add(".wtv"); 
            list.Add(".wvx"); 
            list.Add(".x"); 
            list.Add(".xaf"); 
            list.Add(".xaml"); 
            list.Add(".xap"); 
            list.Add(".xbap"); 
            list.Add(".xbm"); 
            list.Add(".xdr"); 
            list.Add(".xht"); 
            list.Add(".xhtml"); 
            list.Add(".xla"); 
            list.Add(".xlam"); 
            list.Add(".xlc"); 
            list.Add(".xlm"); 
            list.Add(".xls"); 
            list.Add(".xlsb"); 
            list.Add(".xlsm"); 
            list.Add(".xlsx"); 
            list.Add(".xlt"); 
            list.Add(".xltm"); 
            list.Add(".xltx"); 
            list.Add(".xlw"); 
            list.Add(".xml"); 
            list.Add(".xof"); 
            list.Add(".xpm"); 
            list.Add(".xps"); 
            list.Add(".xsd"); 
            list.Add(".xsf"); 
            list.Add(".xsl"); 
            list.Add(".xslt"); 
            list.Add(".xsn"); 
            list.Add(".xtp"); 
            list.Add(".xwd"); 
            list.Add(".z"); 
            list.Add(".zip"); 
            list.Add(".webp"); 
            list.Add(".iso"); 
            list.Add(".apk"); 
            list.Add(".sub"); 
            list.Add(".str"); 
            list.Add(".mkv"); 
            list.Add(".mka");

            return list;
        }

    }
}


