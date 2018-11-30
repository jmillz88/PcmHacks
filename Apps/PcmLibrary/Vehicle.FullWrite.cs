﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcmHacking
{
    public partial class Vehicle
    {
        /// <summary>
        /// Replace the full contents of the PCM.
        /// </summary>
        public async Task<bool> WriteContents(bool kernelRunning, bool recoveryMode, CancellationToken cancellationToken, Stream stream)
        {
            byte[] image = new byte[0];// stream.Length];
//            int bytesRead = await stream.ReadAsync(image, 0, (int)stream.Length);
//            if (bytesRead != stream.Length)
//            {
 //               this.logger.AddUserMessage("Unable to read input file.");
  //              return false;
   //         }

            try
            {
                // This must precede the switch to 4X.
//                ToolPresentNotifier toolPresentNotifier = new ToolPresentNotifier(this.logger, this.messageFactory, this.device);
//                await toolPresentNotifier.Notify();

                this.device.ClearMessageQueue();

                if (!kernelRunning)
                {
                    // switch to 4x, if possible. But continue either way.
                    // if the vehicle bus switches but the device does not, the bus will need to time out to revert back to 1x, and the next steps will fail.
                    if (!await this.VehicleSetVPW4x(VpwSpeed.FourX))
                    {
                        this.logger.AddUserMessage("Stopping here because we were unable to switch to 4X.");
                        return false;
                    }

//                    await toolPresentNotifier.Notify();

                    Response<byte[]> response = await LoadKernelFromFile("micro-kernel.bin");
                    if (response.Status != ResponseStatus.Success)
                    {
                        logger.AddUserMessage("Failed to load kernel from file.");
                        return false;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

//                    await toolPresentNotifier.Notify();

                    // TODO: instead of this hard-coded address, get the base address from the PcmInfo object.
                    if (!await PCMExecute(response.Value, 0xFF9000, cancellationToken))
                    {
                        logger.AddUserMessage("Failed to upload kernel to PCM");

                        return false;
                    }

//                    await toolPresentNotifier.Notify();

                    logger.AddUserMessage("Kernel uploaded to PCM succesfully.");
                }
                
                // TryWaitForKernel will log user messages.
                if (await this.TryWaitForKernel(5))
                {
                    //await toolPresentNotifier.Notify();

                    try
                    {
                        if (!await this.TryFlashUnlockAndErase())
                        {
                            return false;
                        }

//                        await toolPresentNotifier.Notify();
//                        await this.WriteLoop(image, toolPresentNotifier, cancellationToken);
                    }
                    finally
                    {
                        await this.TryFlashLock();
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                this.logger.AddUserMessage("Something went wrong. " + exception.Message);
                this.logger.AddDebugMessage(exception.ToString());
                return false;
            }
            finally
            {
//                await TryWriteKernelReset();
//                await this.Cleanup();
            }
        }

        public async Task<bool> TryWaitForKernel(int maxAttempts)
        {
            logger.AddUserMessage("Waiting for kernel to respond.");

            return await this.SendMessageValidateResponse(
                this.messageFactory.CreateKernelPing(),
                this.messageParser.ParseKernelPingResponse,
                "kernel ping",
                "Kernel is responding.",
                "No response received from the flash kernel.",
                maxAttempts,
                false);
        }

        private async Task<bool> TryFlashUnlockAndErase()
        {
            await this.device.SetTimeout(TimeoutScenario.Maximum);

            // These two messages must be sent in quick succession.
            // The responses may be delayed, which makes acknowledgement hard.

//            this.logger.AddUserMessage("Unlocking and erasing calibration.");
            await this.device.SendMessage(this.messageFactory.CreateFlashUnlockRequest());
            await this.device.SendMessage(this.messageFactory.CreateCalibrationEraseRequest());
            // await this.device.SendMessage(new Message(System.Text.Encoding.ASCII.GetBytes("AT MA")));

            // Just assume success for now?
            return true;

            /*
            for (int sendAttempt = 1; sendAttempt <= 5; sendAttempt++)
            {
                // These two messages must be sent in quick succession.
                await this.device.SendMessage(this.messageFactory.CreateFlashUnlockRequest());
                await this.device.SendMessage(this.messageFactory.CreateCalibrationEraseRequest());

                for (int receiveAttempt = 1; receiveAttempt <= 5; receiveAttempt++)
                {
                    Message message = await this.device.ReceiveMessage();
                    if (message == null)
                    {
                        continue;
                    }

                    Response<bool> response = this.messageParser.ParseFlashKernelSuccessResponse(message);
                    if (response.Status != ResponseStatus.Success)
                    {
                        this.logger.AddDebugMessage("Ignoring message: " + response.Status);
                        continue;
                    }

                    this.logger.AddDebugMessage("Found response, " + (response.Value ? "succeeded." : "failed."));
                    return true;
                }
            }
            */

        }

        private async Task<bool> TryFlashLock()
        {
            return await this.SendMessageValidateResponse(
                this.messageFactory.CreateFlashLockRequest(),
                this.messageParser.ParseFlashLockResponse,
                "flash lock request",
                "Flash memory locked.",
                "Unable to lock flash memory.");
        }

        private async Task<bool> TryWriteKernelReset()
        {
            return await this.SendMessageValidateResponse(
                this.messageFactory.CreateWriteKernelResetRequest(),
                this.messageParser.ParseWriteKernelResetResponse,
                "flash-kernel PCM reset request",
                "PCM reset.",
                "Unable to reset the PCM.");
        }

        private async Task<bool> SendMessageValidateResponse(
            Message message,
            Func<Message, Response<bool>> filter,
            string messageDescription,
            string successMessage,
            string failureMessage,
            int maxAttempts = 5,
            bool pingKernel = true)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                this.logger.AddUserMessage("Sending " + messageDescription);

                if (!await this.TrySendMessage(message, messageDescription, maxAttempts))
                {
                    this.logger.AddUserMessage("Unable to send " + messageDescription);
                    if (pingKernel)
                    {
                        await this.TryWaitForKernel(1);
                    }
                    continue;
                }

                if (!await this.WaitForSuccess(filter, 10))
                {
                    this.logger.AddUserMessage("No " + messageDescription + " response received.");
                    if (pingKernel)
                    {
                        await this.TryWaitForKernel(1);
                    }
                    continue;
                }

                this.logger.AddUserMessage(successMessage);
                return true;
            }

            this.logger.AddUserMessage(failureMessage);
            if (pingKernel)
            {
                await this.TryWaitForKernel(1);
            }
            return false;
        }


        private async Task WriteLoop(byte[] image, ToolPresentNotifier toolPresentNotifier, CancellationToken cancellationToken)
        {
            for (int iterations = 1; iterations < 1000; iterations++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.AddUserMessage("Canceling operation.");
                    return;
                }

                // the kernel doesn't need this message, but it gets the interface ready to hear the data request
                this.logger.AddUserMessage("Waiting for data request.");
                await this.device.SendMessage(new Message(new byte[] { 0x6C, 0x10, 0xF0, 0x36 }));

                Message incoming = await this.device.ReceiveMessage();
                if (incoming == null)
                {
                    this.logger.AddDebugMessage("No data request received.");
                    continue;
                }

                if (this.messageParser.ParseRecoveryModeBroadcast(incoming).Value == true)
                {
                    this.logger.AddUserMessage("PCM has reverted to recovery mode.");
                    return;
                }

                Response<bool> completionResponse = this.messageParser.ParseWriteKernelFlashComplete(incoming);
                if (completionResponse.Value)
                {
                    this.logger.AddUserMessage("Flash complete");
                    return;
                }

                int length;
                int address;
                Response<bool> request = this.messageParser.ParseWriteKernelDataRequest(incoming, out length, out address);
                if (request.Value != true)
                {
                    this.logger.AddDebugMessage("That was not a data request. " + request.Status.ToString());
                    continue;
                }

                byte[] bytes = new byte[12 + length];
                var header = new byte[] { 0x6C, 0x10, 0xF0, 0x36, 0xE2 };
                header.CopyTo(bytes, 0);
                bytes[5] = (byte)(length & 0xFF00 >> 8);
                bytes[6] = (byte)(length & 0xFF);
                bytes[7] = (byte)(address & 0xFF0000 >> 16);
                bytes[8] = (byte)(address & 0xFF00 >> 8);
                bytes[9] = (byte)(address & 0xFF);

                ushort sum = 0;
                for (int index = 0; index < length; index++)
                {
                    byte b = image[index + address];
                    bytes[index + 10] = b;
                    sum += (ushort)b;
                }

                Message dataResponse = new Message(bytes);

                for (int sendAttempt = 1; sendAttempt <= 5; sendAttempt++)
                {
                    if (await this.device.SendMessage(dataResponse))
                    {
                        this.logger.AddDebugMessage("Data sent.");
                        break;
                    }

                    this.logger.AddDebugMessage("Unable to send data, trying again.");
                }
            }
        }

            /*
                        await this.device.SetTimeout(TimeoutScenario.ReadMemoryBlock);


                    labelstatus1.Text = ("Erasing");
                    log("Erasing Calibration Area");
                    FlashEraseCal();
                    labelstatus1.Text = ("Writing");
                    log("Writing");
                    progressBar1.Minimum = (int)start;
                    progressBar1.Maximum = (int)end;
                    while (addr < end && busy == 1 && addr + len != end)
                    {
                        byte[] rx = this.avt_read_vpw();
                        if (tools.ByteArrayCompare(rx, datarequest, datarequest.Length))
                        {

                            len = (uint)rx[5] << 8;
                            len += rx[6];
                            addr = (uint)rx[7] << 16;
                            addr += (uint)rx[8] << 8;
                            addr += rx[9];

                            byte[] tx = new byte[10u + len + 2u];
                            tx[0] = 0x6C;
                            tx[1] = 0x10;
                            tx[2] = 0xF0;
                            tx[3] = 0x36;
                            tx[4] = 0xE2;
                            tx[5] = rx[5];
                            tx[6] = rx[6];
                            tx[7] = rx[7];
                            tx[8] = rx[8];
                            tx[9] = rx[9];
                            Buffer.BlockCopy(PCM.data.image, (int)addr, tx, 10, (int)len);
                            ushort sum = 0;
                            int i = 10;
                            while (i < 10 + len)
                            {
                                sum += (ushort)tx[i];
                                i++;
                            }
                            tx[10 + len] = (byte)(sum >> 8);
                            tx[10 + len + 1] = (byte)(sum & 255);
                            avt_write_vpw(tx);
                            int j = avt_blockrx_ack();
                            if (j != tx.Length) log("sent " + tx.Length + " bytes, but avt says " + j);

                            // tell the vehicle bus were still here
                            avt_send_vpw(testdevicepresent);

                            // update gui
                            this.progressBar1.Value = (int)addr;
                            Application.DoEvents();
                        }
                        if (tools.ByteArrayCompare(rx, flashcyclecomplete, flashcyclecomplete.Length))
                        {
                            addr = end;
                        }
                    }


                if (busy == 1)
                {
                    rc = true;
                    log("Write completed successfully in " + ((stopwatch.Elapsed.Minutes * 60) + stopwatch.Elapsed.Seconds) + " seconds");
                }

                resetPCM();
                //clearDTCs();
                log("Turn off ignition to complete the process and reset DTCs");
            }
        }
        */
    }
}
