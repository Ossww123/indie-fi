using Amazon.Runtime;
using Amazon.S3;

namespace IndieFi.Web.Modules.Media.Infrastructure;

internal static class S3ClientFactory
{
    public static IAmazonS3 Create(MediaObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var serviceUri = new Uri(options.ServiceUrl, UriKind.Absolute);
        var config = new AmazonS3Config
        {
            ServiceURL = serviceUri.GetLeftPart(UriPartial.Authority),
            AuthenticationRegion = options.Region,
            ForcePathStyle = options.ForcePathStyle,
            SignatureMethod = SigningAlgorithm.HmacSHA256,
            UseHttp = serviceUri.Scheme == Uri.UriSchemeHttp,
        };
        var credentials = new BasicAWSCredentials(
            options.AccessKeyId,
            options.SecretAccessKey);
        return new AmazonS3Client(credentials, config);
    }
}
