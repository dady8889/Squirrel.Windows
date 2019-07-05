This is a fork repo of [Squirrel.Windows](https://github.com/Squirrel/Squirrel.Windows)

I want to use Squirrel for my unity project. But there are some issues in unity mono environment. So I dive into the source code and github issues and finally integrate Squirrel into my unity project.

Changes:

- Remove dependency of Splat in Squirrel project and copy source code of Splat to fix compile
- Add Wrapper exe to fix issues [#414](https://github.com/Squirrel/Squirrel.Windows/issues/414) [#1266](https://github.com/Squirrel/Squirrel.Windows/issues/1266)
- Change bsdiff to VCdiff to fix [#651](https://github.com/Squirrel/Squirrel.Windows/issues/651)
  

Workflow:
- Unity build standalone 
- Rename `yourprojectname_Data/Resources/unity default resources` to `yourprojectname_Data/Resources/unity%20default%20resources`
- Copy the unity build files and Squirrel.UnityWrapper build files to the folder `lib/net45/` 
- Create your .nuspec file
- Generate your .nupkg file 
  ```
  ./nuget.exe pack <yourproject.nuspec> -Version <your version number> -BasePath <path> -OutputDirectory <path>
  ```
- Pack
  ```
  ./Squirrel.exe --releasify <yourproject>.<your version number>.nupkg
  ```


Problems:
- SSL: if your server use ssl, you should run codes before any web request
  
  ```
  ServicePointManager.ServerCertificateValidationCallback = MyRemoteCertificateValidationCallback;
 
 ```
    public bool MyRemoteCertificateValidationCallback(System.Object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) {
        bool isOk = true;
        // If there are errors in the certificate chain, look at each error to determine the cause.
        if (sslPolicyErrors != SslPolicyErrors.None) {
            for(int i=0; i<chain.ChainStatus.Length; i++) {
                if(chain.ChainStatus[i].Status != X509ChainStatusFlags.RevocationStatusUnknown) {
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                    chain.ChainPolicy.UrlRetrievalTimeout = new TimeSpan(0, 1, 0);
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
                    bool chainIsValid = chain.Build((X509Certificate2)certificate);
                    if(!chainIsValid) {
                        isOk = false;
                    }
                }
            }
        }
        return isOk;
    }
  ```

Reference:
- https://github.com/Squirrel/Squirrel.Windows/issues/1266#issuecomment-399479349
- https://stackoverflow.com/questions/30109214/is-it-possible-to-package-and-deploy-a-unity3d-app-using-squirrel-for-windows-in
- https://answers.unity.com/questions/792342/how-to-validate-ssl-certificates-when-using-httpwe.html
