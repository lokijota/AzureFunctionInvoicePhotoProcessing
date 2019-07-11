namespace ProcessInvoiceFunction
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using ImageMagick;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Host;
    using Microsoft.Extensions.Logging;
    using System.Linq;

    public static class ProcessInvoice
    {
        [FunctionName("ProcessInvoice")]
        public static void Run([BlobTrigger("inbox/{inputFilename}", Connection = "StorageAccountConnString")]Stream inputStream, string inputFilename,
            [Blob("outbox/out-{inputFilename}", FileAccess.Write, Connection = "StorageAccountConnString")] Stream outputStream, ILogger log)
        {
            log.LogInformation($"STEP: trigger fired for file:{inputFilename}, Size: {inputStream.Length} Bytes");

            using (MagickImage image = new MagickImage(inputStream))
            {
                log.LogInformation($"STEP: Read image {inputFilename} with {image.BaseWidth} x {image.BaseHeight} in {image.Format.ToString()}");

                // Step 0 - does nothing in the sample images I used
                image.AutoOrient();

                // Step 1 - Deskew
                image.BackgroundColor = MagickColor.FromRgb(0, 0, 0);
                image.Deskew(new Percentage(1)); // documentation suggests "A threshold of 40% works for most images" but 1% seems to work well
                IMagickImage rotatedImage = image.Clone();
                log.LogInformation($"STEP: Deskewed {inputFilename}");

                // Step 2 - Apply threshold to transform in black and white image
                image.AutoThreshold(AutoThresholdMethod.OTSU);
                log.LogInformation($"STEP: Applied OTSU to {inputFilename}");

                // Step 3 - find the regions (blocs) in the image and group them if large enough
                IEnumerable<ConnectedComponent> components = null;
                ConnectedComponentsSettings ccs = new ConnectedComponentsSettings();
                ccs.AreaThreshold = 500*500.0; // 500x500 -- seems to be pointless, many more regions are returned
                ccs.Connectivity = 8;
                components = image.ConnectedComponents(ccs);

                // if there are multiple blocs, consolidate them in a larger block
                if(components != null && components.Count() > 0)
                {
                    log.LogInformation($"STEP: Looked for regions in {inputFilename}, there are {components.Count()}");

                    // filter out the smaller rectangles, as the AreaThreshold parameter seems not to be working
                    List<ConnectedComponent> biggerComponents = components.Where(cc => cc.Height * cc.Width >= 250000 && cc.Height * cc.Width != image.Width * image.Height)/*.OrderByDescending(i => i.Height * i.Width)*/.ToList();
                    int topLeftX = biggerComponents[0].X, topLeftY=biggerComponents[0].Y, bottomRightX=biggerComponents[0].Width + topLeftX, bottomRightY = biggerComponents[0].Height + topLeftY;

                    foreach (ConnectedComponent cc in biggerComponents)
                    {
                        #region Debug -- draw the regions on the image
                        //DrawableStrokeColor strokeColor = new DrawableStrokeColor(new MagickColor("yellow"));
                        //DrawableStrokeWidth stokeWidth = new DrawableStrokeWidth(3);
                        //DrawableFillColor fillColor = new DrawableFillColor(new MagickColor(50, 50, 50, 128));
                        //DrawableRectangle dr = new DrawableRectangle(cc.X, cc.Y, cc.X + cc.Width, cc.Y + cc.Height);
                        //rotatedImage.Draw(dr, strokeColor, stokeWidth, fillColor);
                        #endregion

                        if (cc.X < topLeftX)
                        {
                            topLeftX = cc.X;
                        }

                        if (cc.Y < topLeftY)
                        {
                            topLeftY = cc.Y;
                        }

                        if(cc.X + cc.Width > bottomRightX)
                        {
                            bottomRightX = cc.X + cc.Width;
                        }

                        if (cc.Y + cc.Height> bottomRightY)
                        {
                            bottomRightY = cc.Y + cc.Height;
                        }
                    }

                    #region Debug -- draw the bounding box on the image
                    //DrawableStrokeColor strokeColor2 = new DrawableStrokeColor(new MagickColor("purple"));
                    //DrawableStrokeWidth stokeWidth2 = new DrawableStrokeWidth(3);
                    //DrawableFillColor fillColor2 = new DrawableFillColor(new MagickColor(50, 50, 50, 128));
                    //DrawableRectangle dr2 = new DrawableRectangle(topLeftX, topLeftY, bottomRightX, bottomRightY);
                    //rotatedImage.Draw(dr2, strokeColor2, stokeWidth2, fillColor2);
                    #endregion

                    // Step 4 - Crop the image
                    MagickGeometry mg = new MagickGeometry(topLeftX, topLeftY, bottomRightX-topLeftX, bottomRightY-topLeftY);
                    rotatedImage.RePage();  // this is needed because otherwise the crop is relative to the page information and sometimes this leads to an incorrect crop
                    rotatedImage.Crop(mg);

                    log.LogInformation($"STEP: Cropped {inputFilename} to fit existing large regions");
                }
                else
                {
                    log.LogInformation($"STEP: Looked for large regions in {inputFilename}, none were found, skipping crop");
                }
                
                // Step 5 - Resize the image to 1200px width (todo: move to configuration)
                int originalWidth = rotatedImage.BaseWidth;
                int originalHeight = rotatedImage.BaseHeight;
                rotatedImage.Resize(1200, 0); // make width 1200, height proportional
                log.LogInformation($"STEP: Resized {inputFilename} from {originalWidth}x{originalHeight} to {image.BaseWidth}x{image.BaseHeight}");

                // Step 6 - write out as Jpeg with 70% quality
                rotatedImage.Format = MagickFormat.Jpeg;
                rotatedImage.Quality = 70;
                rotatedImage.Write(outputStream);
                log.LogInformation($"STEP: Wrote out {inputFilename} as JPEG");
            }

            log.LogInformation($"STEP: Processing of {inputFilename} done");
        }
    }
}
