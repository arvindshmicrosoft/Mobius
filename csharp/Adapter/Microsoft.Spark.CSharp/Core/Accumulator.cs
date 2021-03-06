﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

using Microsoft.Spark.CSharp.Interop.Ipc;
using Microsoft.Spark.CSharp.Network;
using Microsoft.Spark.CSharp.Services;

[assembly: InternalsVisibleTo("CSharpWorker")]
namespace Microsoft.Spark.CSharp.Core
{
    /// <summary>
    /// A shared variable that can be accumulated, i.e., has a commutative and associative "add"
    /// operation. Worker tasks on a Spark cluster can add values to an Accumulator with the +=
    /// operator, but only the driver program is allowed to access its value, using Value.
    /// Updates from the workers get propagated automatically to the driver program.
    /// 
    /// While <see cref="SparkContext"/> supports accumulators for primitive data types like int and
    /// float, users can also define accumulators for custom types by providing a custom
    /// <see cref="AccumulatorParam{T}"/> object. Refer to the doctest of this module for an example.
    /// 
    /// See python implementation in accumulators.py, worker.py, PythonRDD.scala
    /// 
    /// </summary>
    [Serializable]
    public class Accumulator
    {
        internal static Dictionary<int, Accumulator> accumulatorRegistry = new Dictionary<int, Accumulator>();

        [ThreadStatic] // Thread safe is needed when running in C# worker
        internal static Dictionary<int, Accumulator> threadLocalAccumulatorRegistry = new Dictionary<int, Accumulator>();

        /// <summary>
        /// The identity of the accumulator 
        /// </summary>
        protected int accumulatorId;

        /// <summary>
        /// Indicates whether the accumulator is on driver side.
        /// When deserialized on worker side, isDriver is false by default.
        /// </summary>
        [NonSerialized]
        protected bool isDriver = false;
    }

    /// <summary>
    /// A generic version of <see cref="Accumulator"/> where the element type is specified by the driver program.
    /// </summary>
    /// <typeparam name="T">The type of element in the accumulator.</typeparam>
    [Serializable]
    public class Accumulator<T> : Accumulator
    {
        [NonSerialized]
        internal T value;
        private readonly AccumulatorParam<T> accumulatorParam = new AccumulatorParam<T>();

        /// <summary>
        /// Initializes a new instance of the Accumulator class with a specified identity and a value.
        /// </summary>
        /// <param name="accumulatorId">The Identity of the accumulator</param>
        /// <param name="value">The value of the accumulator</param>
        public Accumulator(int accumulatorId, T value)
        {
            this.accumulatorId = accumulatorId;
            this.value = value;
            isDriver = true;
            accumulatorRegistry[accumulatorId] = this;
        }

        [OnDeserialized()]
        internal void OnDeserializedMethod(System.Runtime.Serialization.StreamingContext context)
        {
            if (threadLocalAccumulatorRegistry == null)
            {
                threadLocalAccumulatorRegistry = new Dictionary<int, Accumulator>();
            }
            if (!threadLocalAccumulatorRegistry.ContainsKey(accumulatorId))
            {
                threadLocalAccumulatorRegistry[accumulatorId] = this;
            }
        }

        /// <summary>
        /// Gets or sets the value of the accumulator; only usable in driver program
        /// </summary>
        /// <exception cref="ArgumentException"></exception>
        public T Value
        {
            // Get the accumulator's value; only usable in driver program
            get
            {
                if (!isDriver)
                {
                    throw new ArgumentException("Accumulator.value cannot be accessed inside tasks");
                }
                return (Accumulator.accumulatorRegistry[accumulatorId] as Accumulator<T>).value;
            }
            // Sets the accumulator's value; only usable in driver program
            set
            {
                if (!isDriver)
                {
                    throw new ArgumentException("Accumulator.value cannot be accessed inside tasks");
                }
                this.value = value;
            }
        }

        /// <summary>
        /// Adds a term to this accumulator's value
        /// </summary>
        /// <param name="term"></param>
        /// <returns></returns>
        public void Add(T term)
        {
            value = accumulatorParam.AddInPlace(value, term);
        }

        /// <summary>
        /// The += operator; adds a term to this accumulator's value
        /// </summary>
        /// <param name="self"></param>
        /// <param name="term"></param>
        /// <returns></returns>
        public static Accumulator<T> operator +(Accumulator<T> self, T term)
        {
            self.Add(term);
            return self;
        }

        /// <summary>
        /// Creates and returns a string representation of the current accumulator
        /// </summary>
        /// <returns>A string representation of the current accumulator</returns>
        public override string ToString()
        {
            return string.Format("Accumulator<id={0}, value={1}>", accumulatorId, value);
        }
    }
    /// <summary>
    /// An AccumulatorParam that uses the + operators to add values. Designed for simple types
    /// such as integers, floats, and lists. Requires the zero value for the underlying type
    /// as a parameter.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [Serializable]
    internal class AccumulatorParam<T>
    {
        /// <summary>
        /// Provide a "zero value" for the type
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        internal T Zero(T value)
        {
            return default(T);
        }
        /// <summary>
        /// Add two values of the accumulator's data type, returning a new value;
        /// </summary>
        /// <param name="value1"></param>
        /// <param name="value2"></param>
        /// <returns></returns>
        internal T AddInPlace(T value1, T value2)
        {
            dynamic d1 = value1, d2 = value2;
            d1 += d2;
            return d1;
        }
    }

    /// <summary>
    /// A simple TCP server that intercepts shutdown() in order to interrupt
    /// our continuous polling on the handler.
    /// </summary>
    internal class AccumulatorServer
    {
        private readonly ILoggerService logger = LoggerServiceFactory.GetLogger(typeof(AccumulatorServer));
        private volatile bool serverShutdown;
        private ISocketWrapper innerSocket;

        internal AccumulatorServer()
        {
            innerSocket = SocketFactory.CreateSocket();
        }

        internal void Shutdown()
        {
            serverShutdown = true;
            innerSocket.Close();
        }

        internal int StartUpdateServer()
        {
            innerSocket.Listen();
            Task.Run(() =>
            {
                try
                {
                    IFormatter formatter = new BinaryFormatter();
                    using (var s = innerSocket.Accept())
                    using (var ns = s.GetStream())
                    {
                        while (!serverShutdown)
                        {
                            int numUpdates = SerDe.ReadInt(ns);
                            for (int i = 0; i < numUpdates; i++)
                            {
                                var ms = new MemoryStream(SerDe.ReadBytes(ns));
                                var update = (Tuple<int, dynamic>)formatter.Deserialize(ms);

                                if (Accumulator.accumulatorRegistry.ContainsKey(update.Item1))
                                {
                                    Accumulator accumulator = Accumulator.accumulatorRegistry[update.Item1];
                                    accumulator.GetType().GetMethod("Add").Invoke(accumulator, new object[] { update.Item2 });
                                }
                                else
                                {
                                    Console.Error.WriteLine("WARN: cann't find update.Key: {0} for accumulator, will create a new one", update.Item1);
                                    var genericAccumulatorType = typeof(Accumulator<>);
                                    var specificAccumulatorType = genericAccumulatorType.MakeGenericType(update.Item2.GetType());
                                    Activator.CreateInstance(specificAccumulatorType, new object[] { update.Item1, update.Item2 });
                                }
                            }
                            ns.WriteByte((byte)1);  // acknowledge byte other than -1
                            ns.Flush();
                        }
                    }
                }
                catch (SocketException e)
                {
                    if (e.ErrorCode != 10004)   // A blocking operation was interrupted by a call to WSACancelBlockingCall - ISocketWrapper.Close canceled Accep() as expected
                        throw e;
                }
                catch (Exception e)
                {
                    logger.LogError(e.ToString());
                    throw;
                }
            });
            
            return (innerSocket.LocalEndPoint as IPEndPoint).Port;
        }
    }
}
